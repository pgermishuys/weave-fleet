/**
 * Which sessions need the user, which are working, and which are quiet: the split the dashboard and the phone inbox
 * share. No Vue.
 */
import type { SessionListItem } from "@/api/client";

export type SessionBucket = "needs-you" | "working" | "recent";

/**
 * Waiting on an answer, or stopped with an error: the user has to act. A workflow run waiting on the user counts too,
 * through the step session its card is in (`workflowWaiting`); that's the run's state, not the session's.
 */
export function sessionBucket(item: SessionListItem, workflowWaiting?: ReadonlySet<string>): SessionBucket {
  if (item.sessionStatus === "waiting_input" || item.sessionStatus === "error" || workflowWaiting?.has(item.session.id)) {
    return "needs-you";
  }
  return item.sessionStatus === "active" ? "working" : "recent";
}

/** When the session last changed, in ms. */
export function sessionUpdatedAt(item: SessionListItem): number {
  const time = item.session.time;
  const value = time?.updated ?? time?.created ?? 0;
  return typeof value === "number" ? value : Date.parse(value) || 0;
}

/**
 * The one rule for a session archived a moment ago: the list says active until the server has answered, but the user
 * has already archived it (waiting on Undo, or the request is on its way), so every list leaves it out.
 */
export function isHiddenWhileArchiving(sessionId: string, pending?: ReadonlySet<string>): boolean {
  return pending?.has(sessionId) ?? false;
}

/**
 * What a fleet-wide count (the server's, polled) should drop for sessions archived a moment ago: each leaves the
 * Working count when it is in a turn (the server's rule: working or stopped on a question), else the Idle count.
 */
export function countsWithoutArchiving(
  counts: { active: number; idle: number },
  sessions: readonly SessionListItem[],
  pending: ReadonlySet<string>,
): { active: number; idle: number } {
  let { active, idle } = counts;
  for (const item of sessions) {
    if (!isTopLevel(item) || item.retentionStatus === "archived" || !isHiddenWhileArchiving(item.session.id, pending)) continue;
    if (item.sessionStatus === "active" || item.sessionStatus === "waiting_input") active = Math.max(0, active - 1);
    else idle = Math.max(0, idle - 1);
  }
  return { active, idle };
}

/** Top-level sessions the user would see: no subagents, nothing hidden. */
export function isTopLevel(item: SessionListItem): boolean {
  return !item.parentSessionId && !(item as SessionListItem & { isHidden?: boolean }).isHidden;
}
