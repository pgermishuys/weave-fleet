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

/** Top-level sessions the user would see: no subagents, nothing hidden. */
export function isTopLevel(item: SessionListItem): boolean {
  return !item.parentSessionId && !(item as SessionListItem & { isHidden?: boolean }).isHidden;
}
