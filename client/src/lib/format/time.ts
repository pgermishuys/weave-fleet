/**
 * Times as Fleet writes them, in one place: relative ("3h", "3 hours ago"), absolute ("12 Oct, 14:07") and the
 * time zone. Absolute times follow the user's locale through Intl, so 24 or 12 hour and the order of day and month are
 * the locale's; a date leaves out the year when it's this year. Every relative time on screen carries the absolute one
 * in its tooltip (`<RelativeTime>` in components/ui does both).
 *
 * Pure functions with no Vue: the phone app imports this file too.
 */

/** Anything Fleet keeps a time as: unix ms, an ISO or SQLite string, a Date. Empty and invalid input formats as "". */
export type TimeInput = number | string | Date | null | undefined;

/** For tests and callers that must pin them. Leave both out to use the user's locale and time zone. */
export interface LocaleOptions {
  locale?: string;
  timeZone?: string;
}

const MINUTE = 60_000;
const HOUR = 60 * MINUTE;
const DAY = 24 * HOUR;
const WEEK = 7 * DAY;
/** From eight weeks on, a relative time is a date: "12 Oct", not "11w" (and never "119w"). */
const DATE_AFTER = 8 * WEEK;

const ZONED = /(?:[zZ]|[+-]\d{2}:?\d{2})$/;
const DATE_ONLY = /^(\d{4})-(\d{2})-(\d{2})$/;

/**
 * Unix ms for any time input, NaN when there is none. A string without a zone is UTC, the way SQLite writes
 * `2026-11-14 09:30:00`; a bare date (`2026-11-14`) is that day's local midnight; digits alone are unix ms. Zero and
 * negative numbers are "no time", as the server sends them.
 */
export function toTimestamp(input: TimeInput): number {
  if (input === null || input === undefined || input === "") return Number.NaN;
  if (input instanceof Date) return input.getTime();
  if (typeof input === "number") return Number.isFinite(input) && input > 0 ? input : Number.NaN;
  const text = input.trim();
  if (/^\d+$/.test(text)) return toTimestamp(Number(text));
  const day = DATE_ONLY.exec(text);
  if (day) return new Date(Number(day[1]), Number(day[2]) - 1, Number(day[3])).getTime();
  const iso = text.includes("T") ? text : text.replace(" ", "T");
  return Date.parse(ZONED.test(iso) ? iso : `${iso}Z`);
}

const formatters = new Map<string, Intl.DateTimeFormat>();

function dateFormat(options: Intl.DateTimeFormatOptions, { locale, timeZone }: LocaleOptions): Intl.DateTimeFormat {
  const key = JSON.stringify([locale ?? null, timeZone ?? null, options]);
  let format = formatters.get(key);
  if (!format) {
    format = new Intl.DateTimeFormat(locale, { ...options, timeZone });
    formatters.set(key, format);
  }
  return format;
}

/** The calendar year, month and day of `ts` where the user is (or in `timeZone`). */
function calendarDay(ts: number, opts: LocaleOptions): string {
  if (!opts.timeZone) {
    const d = new Date(ts);
    return `${d.getFullYear()}-${d.getMonth() + 1}-${d.getDate()}`;
  }
  const parts = dateFormat({ year: "numeric", month: "numeric", day: "numeric" }, { timeZone: opts.timeZone, locale: "en-US" }).formatToParts(ts);
  const part = (type: string) => parts.find((p) => p.type === type)?.value ?? "";
  return `${part("year")}-${part("month")}-${part("day")}`;
}

function sameYear(ts: number, now: number, opts: LocaleOptions): boolean {
  return calendarDay(ts, opts).split("-")[0] === calendarDay(now, opts).split("-")[0];
}

/** The user's time zone ("Africa/Johannesburg"), or null when the runtime doesn't say. */
export function localTimeZone(): string | null {
  return new Intl.DateTimeFormat().resolvedOptions().timeZone || null;
}

/** "2-digit" where the locale keeps a 24-hour clock ("09:05"), "numeric" where it keeps 12 hours ("9:05 AM"). */
function hourStyle(opts: LocaleOptions): "numeric" | "2-digit" {
  const cycle = dateFormat({ hour: "numeric" }, opts).resolvedOptions().hourCycle;
  return cycle === "h23" || cycle === "h24" ? "2-digit" : "numeric";
}

export interface TimeOptions extends LocaleOptions {
  /** "14:07:32": for logs and anything to the second. */
  seconds?: boolean;
}

/** The clock time, "14:07" or "2:07 PM" by the locale. */
export function formatTime(at: TimeInput, opts: TimeOptions = {}): string {
  const ts = toTimestamp(at);
  if (Number.isNaN(ts)) return "";
  return dateFormat({ hour: hourStyle(opts), minute: "2-digit", ...(opts.seconds ? { second: "2-digit" } : {}) }, opts).format(ts);
}

export interface DateOptions extends LocaleOptions {
  /** Reference time, for "is it this year". Defaults to now. */
  now?: number;
  /** "Sat 14 Nov". */
  weekday?: boolean;
  /** Always write the year, even this year's. */
  year?: boolean;
}

/** A day, "12 Oct" or "Oct 12" by the locale; "12 Oct 2025" when it isn't this year. */
export function formatDate(at: TimeInput, opts: DateOptions = {}): string {
  const ts = toTimestamp(at);
  if (Number.isNaN(ts)) return "";
  const withYear = opts.year || !sameYear(ts, opts.now ?? Date.now(), opts);
  return dateFormat(
    { ...(opts.weekday ? { weekday: "short" } : {}), day: "numeric", month: "short", ...(withYear ? { year: "numeric" } : {}) },
    opts,
  ).format(ts);
}

/**
 * A day and its time, "12 Oct, 14:07"; with the year when it isn't this year. This is the tooltip of every relative
 * time, and what to show wherever the exact moment matters.
 */
export function formatDateTime(at: TimeInput, opts: DateOptions & TimeOptions = {}): string {
  const ts = toTimestamp(at);
  if (Number.isNaN(ts)) return "";
  const withYear = opts.year || !sameYear(ts, opts.now ?? Date.now(), opts);
  return dateFormat(
    {
      ...(opts.weekday ? { weekday: "short" } : {}),
      day: "numeric",
      month: "short",
      ...(withYear ? { year: "numeric" } : {}),
      hour: hourStyle(opts),
      minute: "2-digit",
      ...(opts.seconds ? { second: "2-digit" } : {}),
    },
    opts,
  ).format(ts);
}

/** The time alone when it's today, else the day and time: a message's stamp, "14:07" or "12 Oct, 14:07". */
export function formatTimestamp(at: TimeInput, opts: DateOptions & TimeOptions = {}): string {
  const ts = toTimestamp(at);
  if (Number.isNaN(ts)) return "";
  return calendarDay(ts, opts) === calendarDay(opts.now ?? Date.now(), opts) ? formatTime(ts, opts) : formatDateTime(ts, opts);
}

/**
 * A time near now, as short as stays clear: the clock today ("14:05"), with the weekday within six days either way
 * ("Mon 14:05"), else the day and time ("12 Oct, 14:05"). For when something happens next: a retry, a limit's reset.
 */
export function formatWhen(at: TimeInput, opts: DateOptions & TimeOptions = {}): string {
  const ts = toTimestamp(at);
  if (Number.isNaN(ts)) return "";
  const now = opts.now ?? Date.now();
  if (calendarDay(ts, opts) === calendarDay(now, opts)) return formatTime(ts, opts);
  if (Math.abs(ts - now) < 6 * DAY) return dateFormat({ weekday: "short", hour: hourStyle(opts), minute: "2-digit" }, opts).format(ts);
  return formatDateTime(ts, { ...opts, now });
}

export interface RelativeOptions extends LocaleOptions {
  /** Reference time. Defaults to now; pass the ticking `now` a component keeps so the text stays right. */
  now?: number;
  /** For dense lists and tables: "now", "4m", "3h", "5d", "2w". Words otherwise: "just now", "4 minutes ago". */
  compact?: boolean;
}

const relativeFormats = new Map<string, Intl.RelativeTimeFormat>();

/** Words without Intl.RelativeTimeFormat, which the phone app's JavaScript engine (Hermes) doesn't have. */
const plainRelative = {
  format(value: number, unit: string): string {
    const n = Math.abs(value);
    if (value === -1 && unit === "day") return "yesterday";
    if (value === -1 && unit === "week") return "last week";
    const words = `${n} ${unit}${n === 1 ? "" : "s"}`;
    return value < 0 ? `${words} ago` : `in ${words}`;
  },
};

function relativeFormat(locale: string | undefined): Pick<Intl.RelativeTimeFormat, "format"> {
  if (typeof Intl.RelativeTimeFormat !== "function") return plainRelative;
  const key = locale ?? "";
  let format = relativeFormats.get(key);
  if (!format) {
    format = new Intl.RelativeTimeFormat(locale, { numeric: "auto" });
    relativeFormats.set(key, format);
  }
  return format;
}

/**
 * How long ago (or how soon) `at` is, in whole units rounded down:
 *
 *   under a minute either way   "now"   / "just now"
 *   under an hour               "4m"    / "4 minutes ago"
 *   under a day                 "3h"    / "3 hours ago"
 *   under a week                "5d"    / "5 days ago" ("yesterday" for one)
 *   under eight weeks           "2w"    / "2 weeks ago" ("last week" for one)
 *   from eight weeks            the date, "12 Oct" (with the year when it isn't this year)
 *
 * A time in the future reads "in 4m" / "in 4 minutes". Invalid input is "".
 */
export function formatRelative(at: TimeInput, opts: RelativeOptions = {}): string {
  const ts = toTimestamp(at);
  if (Number.isNaN(ts)) return "";
  const now = opts.now ?? Date.now();
  const diff = now - ts;
  const span = Math.abs(diff);
  if (span < MINUTE) return opts.compact ? "now" : "just now";
  if (span >= DATE_AFTER) return formatDate(ts, { ...opts, now });

  const [value, unit, letter] =
    span < HOUR ? [Math.floor(span / MINUTE), "minute", "m"] as const
    : span < DAY ? [Math.floor(span / HOUR), "hour", "h"] as const
    : span < WEEK ? [Math.floor(span / DAY), "day", "d"] as const
    : [Math.floor(span / WEEK), "week", "w"] as const;

  if (opts.compact) return diff >= 0 ? `${value}${letter}` : `in ${value}${letter}`;
  return relativeFormat(opts.locale).format(diff >= 0 ? -value : value, unit);
}
