import type { AccumulatedMessage, AccumulatedToolPart } from "@/lib/client-types";

/**
 * Shell commands the user runs from the composer (`!git status`). The server gives each one a message of role
 * `shell` (`ShellCommands.Role`) holding one tool part: its input is `{ command }`, its output what the command
 * printed, and its metadata how it ended (`exit`, `status`, `truncated`) when the harness says.
 */
export const SHELL_ROLE = "shell";

export type MessageRole = AccumulatedMessage["role"];

/** A message's role as the wire has it: the user's prompt, a command the user ran, or else the agent's side. */
export function toMessageRole(role: unknown): MessageRole {
  if (role === "user") return "user";
  if (role === SHELL_ROLE) return "shell";
  return "assistant";
}

/** The composer's shell mode: a draft that starts with `!`. */
export function isShellDraft(text: string): boolean {
  return text.startsWith("!");
}

/** The command a shell-mode draft runs: everything after the `!`, trimmed. */
export function shellDraftCommand(text: string): string {
  return isShellDraft(text) ? text.slice(1).trim() : "";
}

/**
 * How a command ended: still `running`; `done` (exit 0, or a harness that doesn't report exit codes); `failed`
 * (a non-zero exit); `timeout` or `killed`.
 */
export type ShellCommandState = "running" | "done" | "failed" | "timeout" | "killed";

export interface ShellCommandView {
  id: string;
  command: string;
  output: string;
  state: ShellCommandState;
  /** The exit code, when the harness reports one. */
  exit?: number;
  /** The harness kept only part of the output. */
  truncated: boolean;
}

function asRecord(value: unknown): Record<string, unknown> | undefined {
  return value && typeof value === "object" && !Array.isArray(value) ? value as Record<string, unknown> : undefined;
}

function outputText(output: unknown): string {
  if (typeof output === "string") return output;
  if (output == null) return "";
  return JSON.stringify(output, null, 2);
}

/** What a `shell` message shows, or null when it has no command part yet (the part arrives after the message). */
export function toShellCommandView(message: AccumulatedMessage): ShellCommandView | null {
  const part = message.parts.find((candidate): candidate is AccumulatedToolPart => candidate.type === "tool");
  if (!part) return null;

  const state = asRecord(part.state);
  const input = asRecord(state?.input);
  const metadata = asRecord(state?.metadata);
  const exit = typeof metadata?.exit === "number" ? metadata.exit : undefined;
  const status = typeof metadata?.status === "string" ? metadata.status : undefined;

  let shellState: ShellCommandState;
  if (state?.status === "pending" || state?.status === "running") shellState = "running";
  else if (status === "timeout") shellState = "timeout";
  else if (status === "killed") shellState = "killed";
  else if (state?.status === "error" || (exit !== undefined && exit !== 0)) shellState = "failed";
  else shellState = "done";

  return {
    id: part.partId,
    command: typeof input?.command === "string" ? input.command : "",
    output: outputText(state?.output ?? (state?.status === "error" ? state?.error : undefined)),
    state: shellState,
    exit,
    truncated: metadata?.truncated === true,
  };
}
