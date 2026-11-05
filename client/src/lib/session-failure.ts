/**
 * A session whose last turn stopped on a failure (`turn.failed`), until its next turn starts.
 */

export interface SessionFailure {
  /** ISO time the turn failed. */
  at: string;
  /** The harness's short reason, e.g. "Provider returned 529: overloaded". */
  message: string;
}

export function toSessionFailure(value: unknown): SessionFailure | null {
  if (!value || typeof value !== "object") return null;
  const { at, message } = value as Record<string, unknown>;
  if (typeof at !== "string" || !Number.isFinite(Date.parse(at))) return null;
  return { at, message: typeof message === "string" ? message : "" };
}
