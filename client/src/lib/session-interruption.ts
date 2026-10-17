/**
 * Words for a turn a Fleet restart cut off.
 */

/** A turn a Fleet restart cut off, until the user continues it or dismisses the card. */
export interface SessionInterruption {
  /** ISO time Fleet restarted. */
  at: string;
  /** The session was waiting on a question or permission, which the restart lost. */
  waiting: boolean;
  /** The last call that didn't finish, when Fleet knows it. */
  lastCall: string | null;
  /** Files changed in the session's worktree, when Fleet could count them. */
  changedFiles: number | null;
}

export function toSessionInterruption(value: unknown): SessionInterruption | null {
  if (!value || typeof value !== "object") return null;
  const { at, waiting, lastCall, changedFiles } = value as Record<string, unknown>;
  if (typeof at !== "string" || !Number.isFinite(Date.parse(at))) return null;
  return {
    at,
    waiting: waiting === true,
    lastCall: typeof lastCall === "string" && lastCall.trim() ? lastCall : null,
    changedFiles: typeof changedFiles === "number" && Number.isFinite(changedFiles) ? changedFiles : null,
  };
}

/** The session row's words. */
export function interruptionRowLabel(interruption: SessionInterruption): string {
  return interruption.waiting ? "Cut off · question lost" : "Cut off by a restart";
}

/** "3 files changed in this worktree". */
export function changedFilesText(count: number): string {
  return `${count} ${count === 1 ? "file" : "files"} changed in this worktree`;
}
