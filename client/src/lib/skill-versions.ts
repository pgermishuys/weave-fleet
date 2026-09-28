import { apiFetch } from "@/lib/api-client";
import { extractApiError } from "@/lib/api-error";

/**
 * The user's own versions of Fleet's built-in skills. A version replaces Fleet's copy in the sessions started after
 * it's saved, and Fleet never overwrites it. Improve asks the model for a better version from a session.
 */

/** A built-in skill as Settings lists it. */
export interface BuiltInSkill {
  name: string;
  description: string;
  enabled: boolean;
  /** The user's version sessions get; null for Fleet's. */
  version?: number | null;
  /** Fleet changed its version since the user's was made, or since they last kept theirs. */
  fleetChanged?: boolean;
  /** How many versions the user made, including ones not in use. */
  versionCount?: number;
}

export interface SkillVersionInfo {
  number: number;
  createdAt: string;
  note: string | null;
  sessionId: string | null;
  sessionTitle: string | null;
  active: boolean;
}

/** A skill with its text: Fleet's, the user's active version, and their history (newest first). */
export interface BuiltInSkillDetail {
  name: string;
  description: string;
  enabled: boolean;
  fleetContent: string;
  yourContent: string | null;
  version: number | null;
  fleetChanged: boolean;
  /** When fleetChanged, Fleet's version as it was when the user's was made. */
  fleetBefore: string | null;
  versions: SkillVersionInfo[];
}

/** What the model proposed. Nothing is saved until the user keeps it. */
export interface SkillProposal {
  name: string;
  /** The text the change was made to. */
  base: string;
  /** The user's version it was made to; null for Fleet's. */
  baseVersion: number | null;
  content: string;
  asks: number;
  tokens: { total: number; fromCache: number } | null;
}

export interface ImproveRequest {
  sessionId: string;
  note: string;
  /** The parts of the session the user chose to include, as text. */
  context?: string;
  /** Read the whole conversation in a fork of the session instead of sending context. */
  wholeConversation?: boolean;
}

const PATH = "/api/skills/built-in";

async function send<T>(path: string, fallback: string, init?: RequestInit): Promise<T> {
  const response = await apiFetch(path, init && {
    ...init,
    headers: { "Content-Type": "application/json", ...init.headers },
  });
  if (!response.ok) {
    let message = fallback;
    try {
      const body = (await response.json()) as { error?: unknown };
      message = extractApiError(body.error ?? body, fallback);
    } catch {
      // Not JSON: the fallback says what failed.
    }
    throw new Error(message);
  }
  return (await response.json()) as T;
}

const at = (name: string) => `${PATH}/${encodeURIComponent(name)}`;

export function getSkill(name: string): Promise<BuiltInSkillDetail> {
  return send(at(name), `Couldn't load ${name}.`);
}

export function saveSkillVersion(
  name: string,
  content: string,
  note: string | null,
  sessionId: string | null = null,
): Promise<BuiltInSkillDetail> {
  return send(`${at(name)}/versions`, `Couldn't save your version of ${name}.`, {
    method: "POST",
    body: JSON.stringify({ content, note, sessionId }),
  });
}

export async function readSkillVersion(name: string, version: number): Promise<string> {
  const body = await send<{ content: string }>(`${at(name)}/versions/${version}`, `Couldn't load version ${version} of ${name}.`);
  return body.content;
}

/** Makes one of the user's versions the one sessions get, or Fleet's when version is null. */
export function switchSkillVersion(name: string, version: number | null): Promise<BuiltInSkillDetail> {
  return send(`${at(name)}/active`, `Couldn't switch ${name}.`, {
    method: "PUT",
    body: JSON.stringify({ version }),
  });
}

export function keepMySkillVersion(name: string): Promise<BuiltInSkillDetail> {
  return send(`${at(name)}/keep-mine`, `Couldn't keep your version of ${name}.`, { method: "POST" });
}

export function improveSkill(name: string, request: ImproveRequest): Promise<SkillProposal> {
  return send(`${at(name)}/improve`, `Couldn't ask for a better ${name}.`, {
    method: "POST",
    body: JSON.stringify(request),
  });
}

/** The label for which copy sessions get: "Fleet's" or "Yours · v3". */
export function versionLabel(version: number | null | undefined): string {
  return version ? `Yours · v${version}` : "Fleet's";
}

/** A rough count of tokens for text, for showing what a question costs before it's asked. */
export function estimateTokens(text: string): number {
  return Math.ceil(text.length / 4);
}

/** "~2.4k tokens" or "~380 tokens". */
export function formatTokens(tokens: number): string {
  return tokens >= 1000 ? `~${(tokens / 1000).toFixed(1)}k tokens` : `~${tokens} tokens`;
}

// ── What Improve can include from the session ────────────────────────────────

/** The parts of a conversation message Improve reads. */
export interface ImproveMessage {
  id: string;
  role: string;
  body: string;
  tools?: { title: string; kind?: string; status?: string; output?: string }[];
}

/** The turn a skill was used in, split into the parts the user can include. Empty strings when there's nothing. */
export interface ImproveTurn {
  /** What the user asked, and what the agent wrote back in that turn. */
  exchange: string;
  /** The turn's tool calls, each with the start of its output. */
  toolCalls: string;
}

/** How much of each tool call's output goes in: enough to see what happened, not whole files. */
const TOOL_OUTPUT_LIMIT = 1500;

/**
 * The turn around `messageId`: back to the prompt that started it and on to the next prompt. The agent's messages
 * give the reply and the tool calls.
 */
export function improveTurn(messages: readonly ImproveMessage[], messageId: string): ImproveTurn {
  const index = messages.findIndex((message) => message.id === messageId);
  if (index < 0) return { exchange: "", toolCalls: "" };

  let start = index;
  while (start > 0 && messages[start]?.role !== "user") start -= 1;
  let end = index + 1;
  while (end < messages.length && messages[end]?.role !== "user") end += 1;

  const request = messages[start]?.role === "user" ? messages[start]?.body.trim() ?? "" : "";
  const replies = messages.slice(start, end).filter((message) => message.role !== "user");
  const reply = replies.map((message) => message.body.trim()).filter(Boolean).join("\n\n");
  const exchange = [
    request ? `The user asked:\n${request}` : "",
    reply ? `The agent answered:\n${reply}` : "",
  ].filter(Boolean).join("\n\n");

  const toolCalls = replies
    .flatMap((message) => message.tools ?? [])
    .map((tool) => {
      const head = `- ${tool.kind ?? "tool"}: ${tool.title}${tool.status && tool.status !== "Completed" ? ` (${tool.status})` : ""}`;
      const output = tool.output?.trim();
      if (!output) return head;
      const shown = output.length > TOOL_OUTPUT_LIMIT ? `${output.slice(0, TOOL_OUTPUT_LIMIT)}\n…` : output;
      return `${head}\n${shown.replace(/^/gm, "  ")}`;
    })
    .join("\n");

  return { exchange, toolCalls: toolCalls ? `The tool calls in that turn:\n${toolCalls}` : "" };
}
