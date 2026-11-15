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

