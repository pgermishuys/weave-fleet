/**
 * The phone's conversation, as blocks: what you said, what the agent said, and each run of tool calls as one box of
 * tool rows, as the desktop draws them (Read path ✓, Edit path +1 −1), the first few shown and the rest a tap away.
 * Subagents stay as rows that open the child session; a question the agent asked becomes a quiet line once answered.
 * Presentation only: messages come from the same reducer as the desktop's. No Vue.
 */
import { toolDiffLines, toolDiffText } from "@/components/session/activity-stream-tool-card";
import type { AccumulatedMessage, AccumulatedToolPart } from "@/lib/client-types";
import { parseDiffLines } from "@/lib/diff-parser";
import { getToolDisplayLabel } from "@/lib/tool-icons";
import { getQuestionInput } from "@/lib/question-types";
import { toShellCommandView, type ShellCommandView } from "@/lib/shell-commands";
import { getToolLabel } from "@/lib/tool-labels";

export type PhoneBlock =
  | { kind: "user"; key: string; messageId: string; text: string; createdAt?: number; images: number; steered: boolean }
  | { kind: "text"; key: string; messageId: string; text: string; createdAt?: number }
  | { kind: "steps"; key: string; steps: FoldedStep[]; summary: string; running: boolean; failed: number }
  | { kind: "subagent"; key: string; title: string; agent: string; running: boolean; childSessionId: string | null }
  | { kind: "question"; key: string; question: string; answer: string | null; pending: boolean }
  | { kind: "shell"; key: string; view: ShellCommandView }
  | { kind: "error"; key: string; text: string };

/** One tool call in a folded run. */
export interface FoldedStep {
  id: string;
  tool: string;
  label: string;
  status: "pending" | "running" | "completed" | "error";
  category: StepCategory;
  part: AccumulatedToolPart;
}

export type StepCategory = "read" | "edit" | "run" | "search" | "other";

const SUBAGENT_TOOLS = new Set(["task", "subagent"]);
const READ_TOOLS = new Set(["read", "list", "ls", "webfetch", "fetch"]);
const EDIT_TOOLS = new Set(["edit", "write", "patch", "multiedit", "apply_patch"]);
const RUN_TOOLS = new Set(["bash", "shell"]);
const SEARCH_TOOLS = new Set(["grep", "glob", "search", "codesearch", "websearch"]);

function asRecord(value: unknown): Record<string, unknown> | null {
  return value !== null && typeof value === "object" && !Array.isArray(value) ? (value as Record<string, unknown>) : null;
}

export function stepCategory(tool: string): StepCategory {
  const name = tool.toLowerCase();
  if (READ_TOOLS.has(name)) return "read";
  if (EDIT_TOOLS.has(name)) return "edit";
  if (RUN_TOOLS.has(name)) return "run";
  if (SEARCH_TOOLS.has(name)) return "search";
  return "other";
}

function statusOf(part: AccumulatedToolPart): FoldedStep["status"] {
  const status = asRecord(part.state)?.status;
  return status === "running" || status === "completed" || status === "error" ? status : "pending";
}

function toStep(part: AccumulatedToolPart): FoldedStep {
  const input = asRecord(asRecord(part.state)?.input);
  return {
    id: part.partId || part.callId,
    tool: part.tool,
    label: getToolLabel(part.tool, input),
    status: statusOf(part),
    category: stepCategory(part.tool),
    part,
  };
}

function plural(count: number, one: string, many: string): string {
  return `${count} ${count === 1 ? one : many}`;
}

/** "Read 4 files · edited 1 · ran 1 command · searched 2", first word capitalised. */
export function summarizeSteps(steps: readonly FoldedStep[]): string {
  const count = (category: StepCategory) => steps.filter((step) => step.category === category).length;
  const parts: string[] = [];
  const read = count("read");
  const edited = count("edit");
  const ran = count("run");
  const searched = count("search");
  const other = count("other");
  if (read) parts.push(`read ${plural(read, "file", "files")}`);
  if (edited) parts.push(`edited ${edited}`);
  if (ran) parts.push(`ran ${plural(ran, "command", "commands")}`);
  if (searched) parts.push(`searched ${searched}`);
  if (other) parts.push(`used ${plural(other, "tool", "tools")}`);
  const line = parts.join(" · ") || "Worked";
  return line.charAt(0).toUpperCase() + line.slice(1);
}

function questionBlock(part: AccumulatedToolPart): PhoneBlock | null {
  const input = getQuestionInput(part);
  if (!input?.questions.length) return null;
  const state = asRecord(part.state);
  const status = statusOf(part);
  const metadata = asRecord(state?.metadata);
  const answers = Array.isArray(metadata?.answers) ? metadata.answers : null;
  const answer = answers ? answers.flat().filter((a): a is string => typeof a === "string").join(", ") : typeof state?.output === "string" ? state.output : null;
  return {
    kind: "question",
    key: `q:${part.partId || part.callId}`,
    question: input.questions[0].question,
    answer: status === "completed" ? (answer || "answered") : null,
    pending: status === "pending" || status === "running",
  };
}

function subagentBlock(part: AccumulatedToolPart): PhoneBlock {
  const state = asRecord(part.state);
  const input = asRecord(state?.input);
  const metadata = asRecord(state?.metadata);
  const status = statusOf(part);
  const sessionId = metadata?.sessionId ?? metadata?.sessionID ?? metadata?.childSessionId;
  return {
    kind: "subagent",
    key: `a:${part.partId || part.callId}`,
    title: typeof input?.description === "string" && input.description ? input.description : getToolLabel(part.tool, input),
    agent: typeof input?.subagent_type === "string" ? input.subagent_type : typeof input?.agent === "string" ? input.agent : "agent",
    running: status === "pending" || status === "running",
    childSessionId: typeof sessionId === "string" ? sessionId : null,
  };
}

/** The conversation as phone blocks, oldest first. */
export function foldMessages(messages: readonly AccumulatedMessage[]): PhoneBlock[] {
  const blocks: PhoneBlock[] = [];
  let run: FoldedStep[] = [];

  const flush = () => {
    if (!run.length) return;
    blocks.push({
      kind: "steps",
      key: `s:${run[0].id}`,
      steps: run,
      summary: summarizeSteps(run),
      running: run.some((step) => step.status === "pending" || step.status === "running"),
      failed: run.filter((step) => step.status === "error").length,
    });
    run = [];
  };

  for (const message of messages) {
    if (message.role === "shell") {
      flush();
      const view = toShellCommandView(message);
      if (view) blocks.push({ kind: "shell", key: `sh:${message.messageId}`, view });
      continue;
    }

    if (message.role === "user") {
      flush();
      const text = message.parts.filter((part) => part.type === "text").map((part) => (part as { text: string }).text).join("\n").trim();
      const images = message.parts.filter((part) => part.type === "file").length;
      if (text || images) {
        blocks.push({ kind: "user", key: `u:${message.messageId}`, messageId: message.messageId, text, createdAt: message.createdAt, images, steered: message.steered === true });
      }
      continue;
    }

    for (const [index, part] of message.parts.entries()) {
      if (part.type === "text") {
        if (!part.text.trim()) continue;
        flush();
        blocks.push({ kind: "text", key: `t:${message.messageId}:${index}`, messageId: message.messageId, text: part.text, createdAt: message.createdAt });
      } else if (part.type === "tool") {
        if (part.tool === "question") {
          const question = questionBlock(part);
          if (question) {
            flush();
            blocks.push(question);
          }
        } else if (SUBAGENT_TOOLS.has(part.tool.toLowerCase())) {
          flush();
          blocks.push(subagentBlock(part));
        } else {
          run.push(toStep(part));
        }
      }
    }

    if (message.turnError) {
      flush();
      blocks.push({ kind: "error", key: `e:${message.messageId}`, text: message.turnError.message || "The turn failed." });
    }
  }

  flush();
  return blocks;
}

/** A step as the desktop's tool row draws it: "Edit", the file, and how it ended (+1 −1, ✓, failed, still running). */
export interface StepRow {
  label: string;
  detail: string;
  /** The detail is a search pattern (Grep, Glob), drawn as a pill. */
  pattern: boolean;
  result: "running" | "failed" | "diff" | "done";
  adds: number;
  dels: number;
}

const PATTERN_TOOLS = new Set(["grep", "glob"]);

function lineStats(part: AccumulatedToolPart, input: Record<string, unknown> | null): { adds: number; dels: number } | null {
  const lines = toolDiffLines(part);
  if (lines.length) return { adds: lines.filter((l) => l.type === "add").length, dels: lines.filter((l) => l.type === "remove").length };
  const text = toolDiffText(part);
  if (text) {
    const body = text.split("\n").filter((line) => !/^(Index: |={3,}|-{3} |\+{3} )/.test(line));
    return { adds: body.filter((l) => l.startsWith("+")).length, dels: body.filter((l) => l.startsWith("-")).length };
  }
  if (typeof input?.oldString === "string" && typeof input?.newString === "string") {
    const parsed = parseDiffLines(input.oldString, input.newString);
    return { adds: parsed.filter((l) => l.type === "add").length, dels: parsed.filter((l) => l.type === "remove").length };
  }
  if (typeof input?.content === "string") return { adds: input.content.split("\n").length, dels: 0 };
  return null;
}

export function stepRow(step: FoldedStep): StepRow {
  const input = asRecord(asRecord(step.part.state)?.input);
  const name = step.tool.toLowerCase();
  const path = typeof input?.filePath === "string" && input.filePath ? input.filePath : typeof input?.path === "string" ? input.path : "";
  const pattern = PATTERN_TOOLS.has(name) && typeof input?.pattern === "string" && input.pattern !== "";
  const detail = step.category === "run" && typeof input?.command === "string" ? input.command
    : pattern ? String(input?.pattern)
      : path || step.label;
  const base = { label: getToolDisplayLabel(step.tool), detail, pattern, adds: 0, dels: 0 };
  if (step.status === "running" || step.status === "pending") return { ...base, result: "running" };
  if (step.status === "error") return { ...base, result: "failed" };
  const stats = step.category === "edit" ? lineStats(step.part, input) : null;
  return stats && stats.adds + stats.dels > 0 ? { ...base, result: "diff", ...stats } : { ...base, result: "done" };
}

/** The steps a run shows as rows: the first three, and the rest as "N more steps" (never "1 more step": that one shows). */
export function visibleSteps<T>(steps: readonly T[], shown = 3): { rows: T[]; more: T[] } {
  if (steps.length <= shown + 1) return { rows: [...steps], more: [] };
  return { rows: steps.slice(0, shown), more: steps.slice(shown) };
}

/** One step opened: an edit as its diff lines (coloured), anything else as what ran and what came out. */
export type StepDetail =
  | { kind: "diff"; file: string; adds: number; dels: number; lines: { kind: "add" | "del" | "hunk" | "ctx"; text: string }[] }
  | { kind: "output"; command: string | null; text: string };

export function stepDetail(step: FoldedStep): StepDetail {
  const state = asRecord(step.part.state);
  const input = asRecord(state?.input);
  const row = stepRow(step);
  if (step.category === "edit") {
    let lines: { kind: "add" | "del" | "hunk" | "ctx"; text: string }[] = [];
    const attached = toolDiffLines(step.part);
    const text = toolDiffText(step.part);
    if (attached.length) {
      lines = attached.map((line) => ({ kind: line.type === "add" ? "add" : line.type === "remove" ? "del" : "ctx", text: `${line.type === "add" ? "+" : line.type === "remove" ? "-" : " "} ${line.content.replace(/^[+-]/, "")}` }));
    } else if (text) {
      lines = text.split("\n")
        .filter((line) => !/^(Index: |={3,}|-{3} |\+{3} )/.test(line))
        .map((line) => ({ kind: line.startsWith("@@") ? "hunk" : line.startsWith("+") ? "add" : line.startsWith("-") ? "del" : "ctx", text: line }));
    } else if (typeof input?.oldString === "string" && typeof input?.newString === "string") {
      lines = parseDiffLines(input.oldString, input.newString).map((line) => ({ kind: line.type === "add" ? "add" : line.type === "remove" ? "del" : "ctx", text: `${line.type === "add" ? "+" : line.type === "remove" ? "-" : " "} ${line.content}` }));
    } else if (typeof input?.content === "string") {
      lines = input.content.split("\n").map((line) => ({ kind: "add", text: `+ ${line}` }));
    }
    if (lines.length) {
      return { kind: "diff", file: row.detail.split("/").pop() || row.detail, adds: lines.filter((l) => l.kind === "add").length, dels: lines.filter((l) => l.kind === "del").length, lines };
    }
  }
  const output = state?.output ?? state?.error;
  const text = typeof output === "string" ? output : output == null ? "" : JSON.stringify(output, null, 2);
  return {
    kind: "output",
    command: step.category === "run" ? row.detail : null,
    text: text.trim() || (step.status === "running" || step.status === "pending" ? "Still running…" : "No output."),
  };
}

type StepsBlock = Extract<PhoneBlock, { kind: "steps" }>;
type SubagentBlock = Extract<PhoneBlock, { kind: "subagent" }>;

/** What the conversation draws: blocks, with runs of tool calls and subagents next to each other in one box. */
export type PhoneItem = Exclude<PhoneBlock, StepsBlock | SubagentBlock> | { kind: "tools"; key: string; parts: (StepsBlock | SubagentBlock)[] };

/** Puts tool runs and subagents that follow each other into one box, as the desktop draws a turn's tools together. */
export function groupTools(blocks: readonly PhoneBlock[]): PhoneItem[] {
  const items: PhoneItem[] = [];
  for (const block of blocks) {
    if (block.kind === "steps" || block.kind === "subagent") {
      const last = items[items.length - 1];
      if (last?.kind === "tools") last.parts.push(block);
      else items.push({ kind: "tools", key: `g:${block.key}`, parts: [block] });
    } else {
      items.push(block);
    }
  }
  return items;
}
