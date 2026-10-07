/**
 * A session waiting to retry a failed model call, in words: the header, the session row and the conversation's working
 * line all say it the same way. It comes with the `activity_status` push (`attempt`, `maxAttempts`, `message`, `next`);
 * every field but the status itself is optional, since harnesses say different amounts (OpenCode 2 gives no maximum).
 */

export interface RetryStatus {
  attempt?: number | null;
  maxAttempts?: number | null;
  /** Why the call failed, e.g. "API overloaded (529)". */
  message?: string | null;
  /** When the harness tries again (ISO). */
  next?: string | null;
}

/** "attempt 3 of 10", "attempt 3", or null. */
export function retryAttemptLabel(retry: RetryStatus | null | undefined): string | null {
  if (!retry?.attempt) return null;
  return retry.maxAttempts ? `attempt ${retry.attempt} of ${retry.maxAttempts}` : `attempt ${retry.attempt}`;
}

/** "in 12 s", "in 2 min", "now", or null when the harness didn't say when. */
export function retryCountdown(retry: RetryStatus | null | undefined, now: number): string | null {
  const next = retry?.next ? Date.parse(retry.next) : Number.NaN;
  if (!Number.isFinite(next)) return null;
  const seconds = Math.ceil((next - now) / 1000);
  if (seconds <= 0) return "now";
  if (seconds < 60) return `in ${seconds} s`;
  return `in ${Math.ceil(seconds / 60)} min`;
}

/** The short form for a session row: "Retry 3/10", "Retry 3" or "Retrying". */
export function retryShortLabel(retry: RetryStatus | null | undefined): string {
  if (!retry?.attempt) return "Retrying";
  return retry.maxAttempts ? `Retry ${retry.attempt}/${retry.maxAttempts}` : `Retry ${retry.attempt}`;
}

/** "Retrying · attempt 3 of 10 · in 12 s · API overloaded (529)", leaving out what the harness didn't say. */
export function describeRetry(retry: RetryStatus | null | undefined, now: number): string {
  return ["Retrying", retryAttemptLabel(retry), retryCountdown(retry, now), retry?.message?.trim() || null]
    .filter((part): part is string => Boolean(part))
    .join(" · ");
}
