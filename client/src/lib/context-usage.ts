/**
 * How full a session's context window is, as Fleet records it from the session's harness: the size of the last
 * model call against the model's window. The same shape comes in the session's snapshot (`context`), from
 * `GET /api/sessions/{id}/context`, and live as `context.updated` on the session's topic.
 */

/** One model call's tokens, each kind counted once. `used` is all of them: what the next call starts from. */
export interface ContextCall {
  input: number;
  cacheRead: number;
  cacheWrite: number;
  output: number;
  reasoning: number;
  used: number;
}

/** The context's size at the end of a turn. */
export interface ContextTurn {
  used: number;
  limit: number | null;
  at: string;
  /** The context was compacted since the turn before this one. */
  afterCompaction: boolean;
}

export interface SessionContextUsage {
  sessionId: string;
  /** Tokens in the context as of the last call; null before the first one and right after a compaction. */
  used: number | null;
  /** The model's context window; null when the harness hasn't said. */
  limit: number | null;
  /** How many tokens make the harness compact on its own, when it can tell. */
  compactsAt: number | null;
  modelId: string | null;
  providerId: string | null;
  lastCall: ContextCall | null;
  lastCallAt: string | null;
  compacting: boolean;
  compactedAt: string | null;
  /** Why the last compaction failed, until another one starts. */
  compactionError: string | null;
  /** Oldest first. */
  turns: ContextTurn[];
  updatedAt: string;
}

/** Under 75% the meter stays quiet; from 75% it's getting full; from 90% nearly full. */
export type ContextTone = "ok" | "warn" | "danger";

export const CONTEXT_WARN_PERCENT = 75;
export const CONTEXT_DANGER_PERCENT = 90;

/** Fleet's payload as the client uses it, or null when it isn't one. The server leaves out what's unknown. */
export function toContextUsage(raw: unknown): SessionContextUsage | null {
  if (!isRecord(raw) || typeof raw.sessionId !== "string") {
    return null;
  }

  return {
    sessionId: raw.sessionId,
    used: toCount(raw.used),
    limit: positive(toCount(raw.limit)),
    compactsAt: positive(toCount(raw.compactsAt)),
    modelId: toText(raw.modelId),
    providerId: toText(raw.providerId),
    lastCall: toCall(raw.lastCall),
    lastCallAt: toText(raw.lastCallAt),
    compacting: raw.compacting === true,
    compactedAt: toText(raw.compactedAt),
    compactionError: toText(raw.compactionError),
    turns: Array.isArray(raw.turns) ? raw.turns.flatMap((turn) => toTurn(turn) ?? []) : [],
    updatedAt: toText(raw.updatedAt) ?? "",
  };
}

/** How full the context is, 0–100, or null when the size or the window isn't known. */
export function contextPercent(context: Pick<SessionContextUsage, "used" | "limit"> | null | undefined): number | null {
  if (context?.used == null || context.limit == null) {
    return null;
  }

  return Math.min(100, Math.round((context.used / context.limit) * 100));
}

export function contextTone(percent: number | null): ContextTone {
  if (percent === null) {
    return "ok";
  }
  if (percent >= CONTEXT_DANGER_PERCENT) {
    return "danger";
  }
  return percent >= CONTEXT_WARN_PERCENT ? "warn" : "ok";
}

/**
 * About how many more turns fit before the harness compacts, from how much the context grew over the last few turns
 * since the last compaction. Null when it can't tell: no compaction point, too few turns, or a context that isn't
 * growing.
 */
export function turnsBeforeCompaction(context: SessionContextUsage, sample = 5): number | null {
  if (context.used == null || context.compactsAt == null) {
    return null;
  }

  const sinceCompaction = context.turns.slice(lastCompactionIndex(context.turns));
  const recent = sinceCompaction.slice(-sample);
  if (recent.length < 2) {
    return null;
  }

  const growth = (recent[recent.length - 1].used - recent[0].used) / (recent.length - 1);
  if (growth <= 0) {
    return null;
  }

  return Math.max(0, Math.floor((context.compactsAt - context.used) / growth));
}

/** A token count the way the meter writes it: 950, 17.1k, 200k, 1M. */
export function formatTokens(tokens: number): string {
  if (tokens < 1000) {
    return String(Math.round(tokens));
  }
  if (tokens < 1_000_000) {
    const thousands = tokens / 1000;
    return `${thousands >= 100 ? Math.round(thousands) : trimZero(thousands.toFixed(1))}k`;
  }
  return `${trimZero((tokens / 1_000_000).toFixed(tokens >= 10_000_000 ? 0 : 1))}M`;
}

function lastCompactionIndex(turns: readonly ContextTurn[]): number {
  for (let i = turns.length - 1; i >= 0; i--) {
    if (turns[i].afterCompaction) {
      return i;
    }
  }
  return 0;
}

function trimZero(value: string): string {
  return value.endsWith(".0") ? value.slice(0, -2) : value;
}

function toCall(raw: unknown): ContextCall | null {
  if (!isRecord(raw)) {
    return null;
  }

  const call = {
    input: toCount(raw.input) ?? 0,
    cacheRead: toCount(raw.cacheRead) ?? 0,
    cacheWrite: toCount(raw.cacheWrite) ?? 0,
    output: toCount(raw.output) ?? 0,
    reasoning: toCount(raw.reasoning) ?? 0,
  };
  return { ...call, used: toCount(raw.used) ?? call.input + call.cacheRead + call.cacheWrite + call.output + call.reasoning };
}

function toTurn(raw: unknown): ContextTurn | null {
  if (!isRecord(raw)) {
    return null;
  }

  const used = toCount(raw.used);
  const at = toText(raw.at);
  return used === null || at === null
    ? null
    : { used, limit: positive(toCount(raw.limit)), at, afterCompaction: raw.afterCompaction === true };
}

function toCount(value: unknown): number | null {
  const count = typeof value === "string" ? Number(value) : value;
  return typeof count === "number" && Number.isFinite(count) && count >= 0 ? count : null;
}

function positive(value: number | null): number | null {
  return value !== null && value > 0 ? value : null;
}

function toText(value: unknown): string | null {
  return typeof value === "string" && value.length > 0 ? value : null;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
