/**
 * What the phone session header says: "Working · 1m 12s", "Needs you · 2m", "Finished · 14:44", "Stopped with an
 * error", or "last heard 14:29" when the machine isn't answering. No Vue.
 */
import { clock, duration } from "@/lib/phone/time";

export type HeaderTone = "working" | "needs-you" | "finished" | "error" | "unreachable" | "idle";

export interface HeaderStatusInput {
  /** The session list's status for it: active, waiting_input, error, idle… */
  sessionStatus: string | null;
  /** The live stream's status: busy, retry, delegating, waiting_input, idle. */
  streamStatus: string | null;
  /** When the current turn started (your last message). */
  turnStartedAt: number | null;
  /** When the session last changed. */
  updatedAt: number | null;
  /** When the last message arrived. */
  lastMessageAt: number | null;
  hasMessages: boolean;
  /** Set while the machine isn't answering: when it last did. */
  unreachableSince: number | null;
  now: number;
}

export interface HeaderStatus {
  tone: HeaderTone;
  state: string;
  detail: string;
}

export function headerStatus(input: HeaderStatusInput): HeaderStatus {
  if (input.unreachableSince !== null) {
    return { tone: "unreachable", state: "last heard", detail: input.unreachableSince ? clock(input.unreachableSince) : "never" };
  }

  const waiting = input.streamStatus === "waiting_input" || input.sessionStatus === "waiting_input";
  if (waiting) return { tone: "needs-you", state: "Needs you", detail: duration(input.updatedAt, input.now) };

  if (input.sessionStatus === "error") return { tone: "error", state: "Stopped with an error", detail: "" };

  const working = input.streamStatus === "busy" || input.streamStatus === "retry" || input.streamStatus === "delegating"
    || (input.streamStatus === null && input.sessionStatus === "active");
  if (working) {
    const what = input.streamStatus === "retry" ? "Retrying" : input.streamStatus === "delegating" ? "Delegating" : "Working";
    return { tone: "working", state: what, detail: duration(input.turnStartedAt, input.now) };
  }

  if (input.hasMessages) {
    return { tone: "finished", state: "Finished", detail: input.lastMessageAt ? clock(input.lastMessageAt) : "" };
  }
  return { tone: "idle", state: "Idle", detail: "" };
}
