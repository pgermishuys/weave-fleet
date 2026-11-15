/**
 * How long something took or has been going, one way everywhere: "45 s", "3 min 20 s", "1 h 05 min", "2 d 03 h".
 * Two units at most, the smaller one padded so a ticking counter doesn't change width. Whole units are rounded down,
 * so a running counter never runs ahead of the clock.
 *
 * Pure functions with no Vue: the phone app imports this file too.
 */

export interface DurationOptions {
  /**
   * "seconds" (the default) keeps seconds up to an hour: "3 min 20 s". "minutes" drops them from the first minute on,
   * "3 min", for a counter on a busy screen that shouldn't tick every second.
   */
  precision?: "seconds" | "minutes";
  /** Tenths under ten seconds, "0.4 s", "3.2 s": for a tool call's or a search's run time. */
  tenths?: boolean;
}

const pad = (n: number) => String(n).padStart(2, "0");

/** A length of time in milliseconds as Fleet writes it. Negative is 0; NaN and infinity are "". */
export function formatDuration(ms: number | null | undefined, opts: DurationOptions = {}): string {
  if (ms === null || ms === undefined || !Number.isFinite(ms)) return "";
  const span = Math.max(0, ms);
  if (opts.tenths && span < 10_000) return `${Math.max(0.1, Math.floor(span / 100) / 10).toFixed(1)} s`;

  const seconds = Math.floor(span / 1000);
  if (seconds < 60) return `${seconds} s`;

  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) {
    const s = seconds % 60;
    return opts.precision === "minutes" || s === 0 ? `${minutes} min` : `${minutes} min ${pad(s)} s`;
  }

  const hours = Math.floor(minutes / 60);
  if (hours < 24) return minutes % 60 === 0 ? `${hours} h` : `${hours} h ${pad(minutes % 60)} min`;

  const days = Math.floor(hours / 24);
  return hours % 24 === 0 ? `${days} d` : `${days} d ${pad(hours % 24)} h`;
}

/**
 * How long until something is due, rounded up so it never reads early: "in 12 s", "in 3 min", "in 2 h 05 min";
 * "now" once it's due, "" for no time.
 */
export function formatCountdown(leftMs: number | null | undefined): string {
  if (leftMs === null || leftMs === undefined || !Number.isFinite(leftMs)) return "";
  if (leftMs <= 0) return "now";
  if (leftMs < 59_000) return `in ${Math.ceil(leftMs / 1000)} s`;
  return `in ${formatDuration(Math.ceil(leftMs / 60_000) * 60_000, { precision: "minutes" })}`;
}

/** How long from `from` to `to` (or to `now`), formatted; "" when `from` is missing. */
export function formatElapsedSince(from: number | null | undefined, now: number, opts: DurationOptions = {}): string {
  if (!from || !Number.isFinite(from)) return "";
  return formatDuration(now - from, opts);
}
