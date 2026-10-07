/**
 * Words for a turn a model provider's limit stopped, and for when Fleet tries it again.
 */

/** The limit that stopped a turn: too many requests, a subscription's window used up, or an overloaded provider. */
export type RetryKind = "rate_limit" | "usage_limit" | "overloaded";

/** When Fleet tries a turn a model provider's limit stopped again. */
export interface ScheduledRetry {
  /** ISO time Fleet sends "Continue where you left off.". */
  dueAt: string;
  /** 1 for the first try after the limit, counting up while the limit keeps stopping the session. */
  attempt: number;
  kind: RetryKind;
  /** What the provider said, as the failure card shows it. */
  reason: string;
  /** The provider said when (a reset time, a retry-after); otherwise Fleet picked the wait. */
  providerSaid: boolean;
}

export function toScheduledRetry(value: unknown): ScheduledRetry | null {
  if (!value || typeof value !== "object") return null;
  const { dueAt, attempt, kind, reason, providerSaid } = value as Record<string, unknown>;
  if (typeof dueAt !== "string" || !Number.isFinite(Date.parse(dueAt))) return null;
  if (kind !== "rate_limit" && kind !== "usage_limit" && kind !== "overloaded") return null;
  return {
    dueAt,
    attempt: typeof attempt === "number" ? attempt : 1,
    kind,
    reason: typeof reason === "string" ? reason : "",
    providerSaid: providerSaid === true,
  };
}

const MINUTE_MS = 60_000;
const HOUR_MS = 60 * MINUTE_MS;
const DAY_MS = 24 * HOUR_MS;

/** The failure card's title for a turn a limit stopped. */
export function limitTitle(kind: string | null | undefined): string | null {
  switch (kind) {
    case "usage_limit":
      return "Usage limit reached";
    case "rate_limit":
      return "Rate limited";
    case "overloaded":
      return "The model's provider is overloaded";
    default:
      return null;
  }
}

/** The limit in a few words, for the session row's tooltip. */
export function limitName(kind: RetryKind): string {
  switch (kind) {
    case "usage_limit":
      return "a usage limit";
    case "rate_limit":
      return "a rate limit";
    case "overloaded":
      return "an overloaded provider";
  }
}

/** "14:05", or "Mon 14:05" when it isn't today, or "9 Oct 14:05" more than a week out. */
export function formatRetryClock(dueAt: string | number, now: number = Date.now()): string {
  const due = new Date(typeof dueAt === "number" ? dueAt : Date.parse(dueAt));
  const time = due.toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" });
  const today = new Date(now);
  if (due.toDateString() === today.toDateString()) return time;
  if (due.getTime() - now < 6 * DAY_MS) return `${due.toLocaleDateString(undefined, { weekday: "short" })} ${time}`;
  return `${due.toLocaleDateString(undefined, { day: "numeric", month: "short" })} ${time}`;
}

/** "in 40 s", "in 12 min", "in 2 h 5 min", "in 3 days"; "now" once it's due. */
export function formatRetryIn(dueAt: string | number, now: number = Date.now()): string {
  const left = (typeof dueAt === "number" ? dueAt : Date.parse(dueAt)) - now;
  if (!Number.isFinite(left) || left <= 0) return "now";
  if (left < MINUTE_MS) return `in ${Math.max(1, Math.round(left / 1000))} s`;
  if (left < HOUR_MS) return `in ${Math.ceil(left / MINUTE_MS)} min`;
  if (left < DAY_MS) {
    const hours = Math.floor(left / HOUR_MS);
    const minutes = Math.ceil((left - hours * HOUR_MS) / MINUTE_MS);
    if (minutes === 60) return `in ${hours + 1} h`;
    return minutes > 0 ? `in ${hours} h ${minutes} min` : `in ${hours} h`;
  }
  const days = Math.round(left / DAY_MS);
  return `in ${days} ${days === 1 ? "day" : "days"}`;
}
