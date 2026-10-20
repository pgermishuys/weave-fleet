/**
 * A session's conversation as a Markdown file a person can read: for an issue, a PR or a doc. It is built from Fleet's
 * normalised messages, so every harness reads the same, and it can't be imported back.
 *
 * Fleet does not remove secrets. Tool output is left out by default; `findKeyLines` only counts lines that look like
 * keys so the dialog can say so.
 */
import type { AccumulatedMessage, AccumulatedToolPart } from "@/lib/client-types";
import { describeCompaction, compactionOf } from "@/lib/compaction";
import { getTool, parseToolInput, asRecord, stringField } from "@/lib/tools";
import { getToolLabel } from "@/lib/tool-labels";
import { toolDiffText } from "@/components/session/activity-stream-tool-card";

export type ExportToolMode = "line" | "output" | "off";

export interface ExportOptions {
  /** One line per call, the line with its output, or no tool calls at all. */
  tools: ExportToolMode;
  /** The model's reasoning, as quotes. */
  thinking: boolean;
  /** Cut the session's folder off the front of paths. */
  shortenPaths: boolean;
  /** Times, model and the export date. */
  meta: boolean;
}

export const DEFAULT_EXPORT_OPTIONS: ExportOptions = {
  tools: "line",
  thinking: false,
  shortenPaths: true,
  meta: true,
};

export interface ExportContext {
  title: string;
  harness?: string | null;
  model?: string | null;
  /** The session's folder, cut off paths when `shortenPaths` is on. */
  directory?: string | null;
  exportedAt: Date;
}

export interface ExportResult {
  markdown: string;
  /** Lines that look like keys or tokens. A hint only: Fleet has not looked at everything. */
  keyLines: number;
  /** Messages that made it into the file. */
  turns: number;
}

/** A tool's output past this is cut, with a line saying so; a build log can run to megabytes. */
const MAX_OUTPUT_CHARS = 20_000;

const ANSI = /\u001b\[[0-9;?]*[ -/]*[@-~]/g;

const KEY_PATTERNS: readonly RegExp[] = [
  /\bsk_(live|test)_[A-Za-z0-9]{8,}/,
  /\bsk-[A-Za-z0-9_-]{20,}/,
  /\bAKIA[0-9A-Z]{12,}/,
  /\bgh[pousr]_[A-Za-z0-9]{20,}/,
  /\bxox[baprs]-[A-Za-z0-9-]{10,}/,
  /-----BEGIN [A-Z ]*PRIVATE KEY-----/,
  /\bBearer\s+[A-Za-z0-9._~+/=-]{20,}/,
  /\b[A-Z0-9_]*(?:API_?KEY|SECRET|TOKEN|PASSWORD)[A-Z0-9_]*\s*[=:]\s*["']?[^\s"']{8,}/i,
];

export function looksLikeKey(line: string): boolean {
  return KEY_PATTERNS.some((pattern) => pattern.test(line));
}

export function findKeyLines(markdown: string): number {
  return markdown.split("\n").filter(looksLikeKey).length;
}

/** The session has reasoning to include, so the Thinking option has something to do. */
export function hasReasoning(messages: readonly AccumulatedMessage[]): boolean {
  return messages.some((message) => message.parts.some((part) => part.type === "reasoning" && part.text.trim() !== ""));
}

/** The title as a file name, "Add upload retries" → "add-upload-retries.md". */
export function exportFileName(title: string, fallback: string): string {
  const stem = title
    .toLowerCase()
    .replace(/[^a-z0-9-_]+/g, "-")
    .replace(/^-+|-+$/g, "")
    .slice(0, 80)
    .replace(/-+$/g, "");
  return `${stem || fallback}.md`;
}

function two(value: number): string {
  return String(value).padStart(2, "0");
}

function clock(timestamp: number | undefined): string | undefined {
  if (timestamp === undefined || !Number.isFinite(timestamp)) return undefined;
  const date = new Date(timestamp);
  return `${two(date.getHours())}:${two(date.getMinutes())}`;
}

function stamp(date: Date): string {
  return `${date.getFullYear()}-${two(date.getMonth() + 1)}-${two(date.getDate())} ${two(date.getHours())}:${two(date.getMinutes())}`;
}

/** A code fence longer than any run of backticks inside, so the content can't close it early. */
function fenced(body: string, language = ""): string {
  const longest = Math.max(0, ...(body.match(/`+/g) ?? []).map((run) => run.length));
  const fence = "`".repeat(Math.max(3, longest + 1));
  return `${fence}${language}\n${body}\n${fence}`;
}

function quoted(text: string): string {
  return text
    .split("\n")
    .map((line) => (line === "" ? ">" : `> ${line}`))
    .join("\n");
}

function inlineCode(text: string): string {
  const longest = Math.max(0, ...(text.match(/`+/g) ?? []).map((run) => run.length));
  const tick = "`".repeat(longest + 1);
  return `${tick}${text}${tick}`;
}

function singleLine(text: string): string {
  return text.replace(/\s*\n\s*/g, " ").trim();
}

function toolOutput(part: AccumulatedToolPart): string | undefined {
  const state = asRecord(part.state);
  if (!state) return undefined;
  for (const key of ["output", "result", "content", "error", "message", "stdout", "stderr"]) {
    const value = state[key];
    if (value == null) continue;
    const text = typeof value === "string" ? value : JSON.stringify(value, null, 2);
    return text.replace(ANSI, "");
  }
  return undefined;
}

/**
 * The change an edit made, as a diff: the unified diff the harness kept, or the patches of the files it changed
 * (OpenCode 2 keeps them in `metadata.files`), or else the strings the call swapped.
 */
function editDiff(part: AccumulatedToolPart): string | undefined {
  const kept = toolDiffText(part);
  if (kept) return kept;
  const state = asRecord(part.state);
  const files = asRecord(state?.metadata)?.files;
  if (Array.isArray(files)) {
    const patches = files.flatMap((file) => {
      const patch = asRecord(file)?.patch;
      return typeof patch === "string" && patch.trim() !== "" ? [patch.trimEnd()] : [];
    });
    if (patches.length > 0) return patches.join("\n");
  }
  const input = parseToolInput(state?.input);
  if (input.oldString !== undefined && input.newString !== undefined) {
    const side = (prefix: string, text: string) => text.split("\n").map((line) => `${prefix}${line}`).join("\n");
    return `${side("-", input.oldString)}\n${side("+", input.newString)}`;
  }
  return undefined;
}

function cut(output: string): string {
  const trimmed = output.replace(/\s+$/, "");
  if (trimmed.length <= MAX_OUTPUT_CHARS) return trimmed;
  return `${trimmed.slice(0, MAX_OUTPUT_CHARS)}\n… ${trimmed.length - MAX_OUTPUT_CHARS} more characters left out`;
}

class Writer {
  private readonly shorten: (text: string) => string;
  /** Whether the last thing written was an assistant turn, so its next step doesn't repeat it. */
  private openTurn = false;

  constructor(
    private readonly options: ExportOptions,
    private readonly context: ExportContext,
  ) {
    const folder = options.shortenPaths ? (context.directory ?? "").replace(/[\\/]+$/, "") : "";
    this.shorten = folder === ""
      ? (text) => text
      : (text) => text.split(`${folder}/`).join("").split(`${folder}\\`).join("");
  }

  toolBlock(part: AccumulatedToolPart): string | undefined {
    const tool = getTool(part.tool);
    const state = asRecord(part.state);
    const input = parseToolInput(state?.input);

    // A page the agent showed is part of the story, whatever the tool setting says.
    if (tool.name === "fleet_page_show") {
      const title = stringField(state, "title") ?? input.raw.title;
      const name = typeof title === "string" && title.trim() !== "" ? title : stringField(input.raw, "path") ?? "page";
      return quoted(`Showed page: "${singleLine(this.shorten(name))}"`);
    }
    if (this.options.tools === "off") return undefined;

    // A file tool names its whole path (the label cuts long ones short); the shortening below is the user's choice.
    const label = singleLine(tool.category === "read" || tool.category === "edit"
      ? input.filePath ?? getToolLabel(part.tool, input.raw) ?? ""
      : getToolLabel(part.tool, input.raw) ?? "");
    const heading = tool.heading || part.tool;
    // A label that only repeats the tool's name adds nothing.
    const subject = label && label.toLowerCase() !== part.tool.toLowerCase() && label.toLowerCase() !== heading.toLowerCase()
      ? ` ${inlineCode(this.shorten(tool.category === "shell" && input.command ? singleLine(input.command).slice(0, 120) : label))}`
      : "";
    const status = String(state?.status ?? "").toLowerCase();
    const note = status === "error" ? " (failed)" : status === "pending" || status === "running" ? " (did not finish)" : "";
    const line = `- ${heading}${subject}${note}`;
    if (this.options.tools !== "output") return line;

    const diff = tool.category === "edit" ? editDiff(part) : undefined;
    if (diff) return `${line}\n\n${fenced(this.shorten(cut(diff)), "diff")}`;
    const output = toolOutput(part);
    if (output && output.trim() !== "") {
      return `${line}\n\n${fenced(this.shorten(cut(output)), tool.category === "shell" ? "text" : "")}`;
    }
    return line;
  }

  message(message: AccumulatedMessage): string[] {
    if (message.compactionSummary) return [];
    if (compactionOf(message)) this.openTurn = false;
    const blocks: string[] = [];

    const compaction = compactionOf(message);
    if (compaction) blocks.push("---", `_${describeCompaction(compaction)}_`);

    const body: string[] = [];
    for (const part of message.parts) {
      if (part.type === "text") {
        const text = part.text.trim();
        if (text !== "") body.push(text);
      } else if (part.type === "reasoning") {
        const text = part.text.trim();
        if (this.options.thinking && text !== "") body.push(quoted(`**Thinking**\n\n${text}`));
      } else if (part.type === "file") {
        // Never the bytes: a data address can be megabytes.
        body.push(`[${part.mime.startsWith("image/") ? "image" : "file"}: ${part.filename || part.mime || "attachment"}]`);
      } else if (part.type === "tool") {
        const block = this.toolBlock(part);
        if (block !== undefined) body.push(block);
      }
    }
    if (body.length === 0) return blocks;

    if (message.command) {
      const args = message.command.arguments ? ` ${message.command.arguments}` : "";
      body.splice(0, body.length, inlineCode(`/${message.command.name}${args}`));
    }

    const who = message.role === "assistant" ? "Assistant" : message.role === "shell" ? "You ran a command" : "You";
    const meta = this.options.meta
      ? [clock(message.createdAt), message.role === "assistant" ? message.modelID : undefined].filter(Boolean).join(" · ")
      : "";
    // An agent's steps arrive as messages of their own; one reply reads as one turn.
    const heading = `## ${who}${meta ? ` · ${meta}` : ""}`;
    const sameTurn = message.role === "assistant" && this.openTurn;
    this.openTurn = message.role === "assistant";
    if (!sameTurn) blocks.push(heading);
    blocks.push(...body);
    return blocks;
  }

  header(): string[] {
    const blocks = [`# ${singleLine(this.context.title) || "Conversation"}`];
    if (this.options.meta) {
      const line = [this.context.harness, this.context.model, `exported ${stamp(this.context.exportedAt)}`].filter(Boolean).join(" · ");
      blocks.push(`_${line}_`);
    }
    return blocks;
  }
}

export function buildMarkdown(
  messages: readonly AccumulatedMessage[],
  options: ExportOptions,
  context: ExportContext,
): ExportResult {
  const writer = new Writer(options, context);
  const blocks = writer.header();
  for (const message of messages) blocks.push(...writer.message(message));
  const turns = blocks.filter((block) => block.startsWith("## ")).length;

  // Tool lines sit tight in a list; everything else is a paragraph apart.
  let markdown = "";
  blocks.forEach((block, index) => {
    if (index > 0) markdown += block.startsWith("- ") && !block.includes("\n") && blocks[index - 1].startsWith("- ") && !blocks[index - 1].includes("\n") ? "\n" : "\n\n";
    markdown += block;
  });
  markdown += "\n";
  return { markdown, keyLines: findKeyLines(markdown), turns };
}
