/**
 * When an automation runs, read out of the sentence that describes it ("every Monday at 9, summarise the open PRs").
 * No model is asked: a handful of patterns cover the phrases people use, and anything else leaves the When chip
 * asking. Times are on the browser's clock; the automation stores that zone, and the server reads them in it.
 */

/** Hours and minutes, 24-hour. */
export type Hm = readonly [number, number];

export type When =
  /** One run. `date` (YYYY-MM-DD) once it's fixed; before that, the next `day` of the week, or today/tomorrow. */
  | { kind: "once"; t: Hm; day?: number; rel?: "today" | "tomorrow"; date?: string }
  /** Days of the week (0 = Sunday). `ambiguous` when the sentence said "on Friday": every week, or just once? */
  | { kind: "weekly"; days: readonly number[]; t: Hm; ambiguous?: boolean }
  | { kind: "hours"; n: number }
  | { kind: "minutes"; n: number }
  | { kind: "monthly"; dom: number; t: Hm }
  | { kind: "cron"; expr: string }
  | { kind: "event"; eventType: string };

/** A schedule found in the text, and where, so the box can highlight it. */
export interface ScheduleHit {
  when: When;
  start: number;
  end: number;
}

const DOW = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
const DOW_LONG = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
const MON = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

const pad = (n: number) => String(n).padStart(2, "0");
export const hm = ([h, m]: Hm) => `${pad(h)}:${pad(m)}`;
export const dayDate = (d: Date) => `${DOW[d.getDay()]} ${d.getDate()} ${MON[d.getMonth()]}`;
const ord = (n: number) => n + (n % 10 === 1 && n !== 11 ? "st" : n % 10 === 2 && n !== 12 ? "nd" : n % 10 === 3 && n !== 13 ? "rd" : "th");

// ── The parser ────────────────────────────────────────────────────────────────

const DAY = "(sun(?:day)?|mon(?:day)?|tue(?:s(?:day)?)?|wed(?:nesday)?|thu(?:r(?:s(?:day)?)?)?|fri(?:day)?|sat(?:urday)?)";
const DAY_INDEX: Record<string, number> = { sun: 0, mon: 1, tue: 2, wed: 3, thu: 4, fri: 5, sat: 6 };
const dayIndex = (word: string) => DAY_INDEX[word.toLowerCase().slice(0, 3)];
const PART = "(?:\\s+(?:in\\s+the\\s+)?(morning|afternoon|evening|night))?";
const TIME = "(?:\\s*,?\\s+(?:at\\s+)?(noon|midnight|\\d{1,2}:\\d{2}\\s*(?:am|pm)?|\\d{1,2}\\s*(?:am|pm))|\\s*,?\\s+at\\s+(\\d{1,2}))?";
const LEAD = "(?:(?:that|which|to)\\s+)?(?:runs?\\s+)?";
const PART_TIME: Record<string, Hm> = { morning: [9, 0], afternoon: [14, 0], evening: [18, 0], night: [21, 0] };

function parseTime(text: string | undefined): Hm | null {
  if (!text) return null;
  const t = text.toLowerCase().trim();
  if (t === "noon") return [12, 0];
  if (t === "midnight") return [0, 0];
  const m = /(\d{1,2})(?::(\d{2}))?\s*(am|pm)?/.exec(t);
  if (!m) return null;
  let h = Number(m[1]);
  const min = m[2] ? Number(m[2]) : 0;
  if (m[3] === "pm" && h < 12) h += 12;
  if (m[3] === "am" && h === 12) h = 0;
  if (h > 23 || min > 59) return null;
  return [h, min];
}

function timeFrom(part: string | undefined, t1: string | undefined, t2: string | undefined): Hm {
  return parseTime(t1 || t2) ?? (part ? PART_TIME[part.toLowerCase()] : undefined) ?? [9, 0];
}

function daysIn(list: string): number[] {
  const out: number[] = [];
  for (const word of list.match(new RegExp(DAY, "gi")) ?? []) {
    const i = dayIndex(word);
    if (!out.includes(i)) out.push(i);
  }
  return out.sort((a, b) => a - b);
}

interface Pattern {
  re: RegExp;
  make: (m: RegExpExecArray) => When;
}

const pattern = (source: string, make: Pattern["make"]): Pattern => ({ re: new RegExp(source, "i"), make });

// First match wins, so the specific phrases come before the general ones.
const PATTERNS: Pattern[] = [
  pattern(`\\b${LEAD}(?:just\\s+)?once\\s+(?:on\\s+)?(?:a\\s+|the\\s+|this\\s+|next\\s+|coming\\s+)?(?:${DAY}|(tomorrow|today))(?![\\w'])${PART}${TIME}`,
    (m) => ({ kind: "once", ...(m[1] ? { day: dayIndex(m[1]) } : { rel: m[2].toLowerCase() as "today" | "tomorrow" }), t: timeFrom(m[3], m[4], m[5]) })),
  pattern(`\\b(?:next|this\\s+coming)\\s+${DAY}(?![\\w'])${PART}${TIME}`,
    (m) => ({ kind: "once", day: dayIndex(m[1]), t: timeFrom(m[2], m[3], m[4]) })),
  pattern(`\\btomorrow(?![\\w'])${PART}${TIME}`,
    (m) => ({ kind: "once", rel: "tomorrow", t: timeFrom(m[1], m[2], m[3]) })),
  pattern(`\\b${LEAD}every\\s+(\\d{1,3})\\s*(minutes?|mins?|hours?|hrs?)\\b`,
    (m) => (/^m/i.test(m[2]) ? { kind: "minutes", n: Number(m[1]) } : { kind: "hours", n: Number(m[1]) })),
  pattern(`\\b${LEAD}(?:every\\s+hour|hourly)\\b`, () => ({ kind: "hours", n: 1 })),
  pattern(`\\b${LEAD}(?:(?:every|on)\\s+)?week\\s?days?\\b${PART}${TIME}`,
    (m) => ({ kind: "weekly", days: [1, 2, 3, 4, 5], t: timeFrom(m[1], m[2], m[3]) })),
  pattern(`\\b${LEAD}(?:every|each)\\s+(${DAY}s?(?:\\s*(?:,|and|&)\\s*${DAY}s?)*)(?![\\w'])${PART}${TIME}`,
    // The day list, then each DAY group, then PART and TIME: the last three captures.
    (m) => ({ kind: "weekly", days: daysIn(m[1]), t: timeFrom(m[m.length - 3], m[m.length - 2], m[m.length - 1]) })),
  pattern(`\\b(sundays|mondays|tuesdays|wednesdays|thursdays|fridays|saturdays)\\b${PART}${TIME}`,
    (m) => ({ kind: "weekly", days: [dayIndex(m[1])], t: timeFrom(m[2], m[3], m[4]) })),
  pattern(`\\b${LEAD}(?:every\\s+(day|morning|evening|night)|daily|each\\s+day|nightly)\\b${PART}${TIME}`,
    (m) => ({
      kind: "weekly",
      days: [0, 1, 2, 3, 4, 5, 6],
      t: timeFrom(m[2] || (m[1] && m[1].toLowerCase() !== "day" ? m[1] : undefined) || (/nightly/i.test(m[0]) ? "night" : undefined), m[3], m[4]),
    })),
  pattern(`\\b${LEAD}(?:on\\s+the\\s+(\\d{1,2})(?:st|nd|rd|th)?\\s+of\\s+(?:every|each)\\s+month|(?:every|each)\\s+month(?:\\s+on\\s+the\\s+(\\d{1,2})(?:st|nd|rd|th)?)?|monthly)${TIME}`,
    (m) => ({ kind: "monthly", dom: Math.max(1, Math.min(28, Number(m[1] || m[2] || 1))), t: timeFrom(undefined, m[3], m[4]) })),
  pattern(`\\b${LEAD}on\\s+(?:a\\s+)?${DAY}(?![\\w'])${PART}${TIME}`,
    // "on a Monday" means any Monday; "on Friday" could be this Friday or every Friday.
    (m) => ({ kind: "weekly", days: [dayIndex(m[1])], t: timeFrom(m[2], m[3], m[4]), ...(/on\s+a\s+/i.test(m[0]) ? {} : { ambiguous: true }) })),
];

/** The schedule in a sentence, or null when there's none it recognises. */
export function parseSchedule(text: string): ScheduleHit | null {
  for (const { re, make } of PATTERNS) {
    const m = re.exec(text);
    if (m) {
      return { when: make(m), start: m.index, end: m.index + m[0].length };
    }
  }
  return null;
}

/**
 * What the agent is asked to do: the text without the schedule words, and without "create an automation that…",
 * which is addressed to Fleet rather than the agent.
 */
export function promptFrom(text: string, hit: ScheduleHit | null): string {
  let s = hit ? `${text.slice(0, hit.start)} ${text.slice(hit.end)}` : text;
  s = s.replace(/^\s*(?:please\s+)?(?:can\s+you\s+)?(?:create|make|set\s*up|add|schedule)\s+(?:me\s+)?(?:an?\s+)?(?:automation|job|task)\b\s*(?:that|which|to|for)?\s*/i, "");
  s = s.replace(/[ \t]{2,}/g, " ").replace(/[ \t]+([,.;:])/g, "$1").replace(/^[\s,;:.\-–]+/, "").replace(/^(?:to|and|then)\s+/i, "").replace(/[\s,;:\-–]+$/, "");
  return s ? s[0].toUpperCase() + s.slice(1) : "";
}

/** A name from the prompt's first few words, for when none is given. */
export function autoName(prompt: string): string {
  if (!prompt) return "";
  const words = prompt.replace(/[.,:;!?](?:\s|$)[\s\S]*$/, "").split(/\s+/).slice(0, 6).join(" ");
  return words.length > 42 ? `${words.slice(0, 40).trimEnd()}…` : words;
}

/** The same schedule at another time of day; a weekly one also stops asking "every week, or once?". */
export function withTime(when: When, t: Hm): When {
  switch (when.kind) {
    case "once":
      return { ...when, t };
    case "weekly":
      return { kind: "weekly", days: when.days, t };
    case "monthly":
      return { ...when, t };
    default:
      return when;
  }
}

// ── Dates ─────────────────────────────────────────────────────────────────────

function at(day: Date, [h, m]: Hm): Date {
  const d = new Date(day);
  d.setHours(h, m, 0, 0);
  return d;
}

/** The date part of a local date as YYYY-MM-DD. */
export function localDate(d: Date): string {
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}

/** When it runs next after `from`, on this clock; null for cron (the server knows) and events. */
export function nextRun(when: When, from: Date = new Date()): Date | null {
  switch (when.kind) {
    case "once": {
      if (when.date) {
        const [y, mo, d] = when.date.split("-").map(Number);
        return at(new Date(y, mo - 1, d), when.t);
      }
      const d = at(from, when.t);
      if (when.rel === "tomorrow") {
        d.setDate(d.getDate() + 1);
      } else if (when.rel === "today") {
        if (d <= from) d.setDate(d.getDate() + 1);
      } else {
        d.setDate(d.getDate() + (((when.day ?? 1) - d.getDay() + 7) % 7));
        if (d <= from) d.setDate(d.getDate() + 7);
      }
      return d;
    }
    case "weekly": {
      for (let i = 0; i < 8; i++) {
        const d = at(from, when.t);
        d.setDate(d.getDate() + i);
        if (when.days.includes(d.getDay()) && d > from) return d;
      }
      return null;
    }
    case "hours": {
      const d = new Date(from);
      d.setMinutes(0, 0, 0);
      do d.setHours(d.getHours() + 1); while (d.getHours() % when.n !== 0);
      return d;
    }
    case "minutes": {
      const d = new Date(from);
      d.setSeconds(0, 0);
      do d.setMinutes(d.getMinutes() + 1); while (d.getMinutes() % when.n !== 0);
      return d;
    }
    case "monthly": {
      const d = new Date(from.getFullYear(), from.getMonth(), when.dom, when.t[0], when.t[1]);
      if (d <= from) d.setMonth(d.getMonth() + 1);
      return d;
    }
    default:
      return null;
  }
}

/** The cron expression for a repeating schedule; null for a one-off or an event. */
export function cronOf(when: When): string | null {
  switch (when.kind) {
    case "weekly": {
      const days = when.days.length === 7 ? "*" : when.days.join(",") === "1,2,3,4,5" ? "1-5" : when.days.join(",");
      return `${when.t[1]} ${when.t[0]} * * ${days}`;
    }
    case "hours":
      return when.n === 1 ? "0 * * * *" : `0 */${when.n} * * *`;
    case "minutes":
      return `*/${when.n} * * * *`;
    case "monthly":
      return `${when.t[1]} ${when.t[0]} ${when.dom} * *`;
    case "cron":
      return when.expr;
    default:
      return null;
  }
}

/** The trigger the server stores. A one-off is fixed to a date here, so it means the same thing tomorrow. */
export function toTrigger(when: When, from: Date = new Date()): { triggerType: string; triggerConfig: string } {
  if (when.kind === "event") {
    return { triggerType: "event", triggerConfig: JSON.stringify({ eventType: when.eventType }) };
  }
  if (when.kind === "once") {
    const d = nextRun(when, from)!;
    return { triggerType: "once", triggerConfig: `${localDate(d)}T${hm([d.getHours(), d.getMinutes()])}` };
  }
  return { triggerType: "schedule", triggerConfig: cronOf(when)! };
}

const NUM = /^\d{1,2}$/;

/** A stored trigger as a When: the cron shapes the composer writes come back as the schedule they were. */
export function fromTrigger(triggerType: string, triggerConfig: string): When | null {
  if (triggerType === "event") {
    try {
      const parsed = JSON.parse(triggerConfig) as { eventType?: unknown };
      return typeof parsed.eventType === "string" ? { kind: "event", eventType: parsed.eventType } : null;
    } catch {
      return null;
    }
  }
  if (triggerType === "once") {
    const m = /^(\d{4}-\d{2}-\d{2})T(\d{2}):(\d{2})$/.exec(triggerConfig.trim());
    return m ? { kind: "once", date: m[1], t: [Number(m[2]), Number(m[3])] } : null;
  }
  if (triggerType !== "schedule") return null;

  const expr = triggerConfig.trim();
  const [min, hour, dom, month, dow, ...rest] = expr.split(/\s+/);
  if (rest.length > 0 || month !== "*") return { kind: "cron", expr };
  if (min === "0" && hour === "*" && dom === "*" && dow === "*") return { kind: "hours", n: 1 };
  const everyHours = /^\*\/(\d{1,2})$/.exec(hour ?? "");
  if (min === "0" && everyHours && dom === "*" && dow === "*") return { kind: "hours", n: Number(everyHours[1]) };
  const everyMinutes = /^\*\/(\d{1,2})$/.exec(min ?? "");
  if (everyMinutes && hour === "*" && dom === "*" && dow === "*") return { kind: "minutes", n: Number(everyMinutes[1]) };
  if (!NUM.test(min ?? "") || !NUM.test(hour ?? "")) return { kind: "cron", expr };

  const t: Hm = [Number(hour), Number(min)];
  if (dom === "*" && dow === "*") return { kind: "weekly", days: [0, 1, 2, 3, 4, 5, 6], t };
  if (dom === "*" && dow === "1-5") return { kind: "weekly", days: [1, 2, 3, 4, 5], t };
  if (dom === "*" && /^[0-6](,[0-6])*$/.test(dow ?? "")) {
    return { kind: "weekly", days: [...new Set(dow!.split(",").map(Number))].sort((a, b) => a - b), t };
  }
  if (NUM.test(dom ?? "") && dow === "*") return { kind: "monthly", dom: Number(dom), t };
  return { kind: "cron", expr };
}

// ── Words ─────────────────────────────────────────────────────────────────────

function daysLabel(days: readonly number[]): string {
  if (days.length === 7) return "Every day";
  if (days.join(",") === "1,2,3,4,5") return "Weekdays";
  if (days.length === 1) return `${DOW_LONG[days[0]]}s`;
  const names = days.map((d) => DOW[d]);
  return `${names.slice(0, -1).join(", ")} and ${names[names.length - 1]}`;
}

/** The When chip: "Mondays 09:00", "Once · Mon 21 Sep", "When?". */
export function whenChip(when: When | null, from: Date = new Date()): string {
  if (!when) return "When?";
  switch (when.kind) {
    case "once":
      return `Once · ${dayDate(nextRun(when, from)!)}`;
    case "weekly":
      return `${daysLabel(when.days)} ${hm(when.t)}`;
    case "hours":
      return when.n === 1 ? "Every hour" : `Every ${when.n} hours`;
    case "minutes":
      return `Every ${when.n} min`;
    case "monthly":
      return `Monthly, ${ord(when.dom)}`;
    case "cron":
      return when.expr;
    case "event":
      return "On an event";
  }
}

/** The schedule in a sentence: "every Monday at 09:00", "once, on Mon 21 Sep at 09:00". */
export function whenSentence(when: When, from: Date = new Date()): string {
  switch (when.kind) {
    case "once":
      return `once, on ${dayDate(nextRun(when, from)!)} at ${hm(when.t)}`;
    case "weekly": {
      const days = when.days.length === 7
        ? "every day"
        : when.days.join(",") === "1,2,3,4,5"
          ? "on weekdays"
          : when.days.length === 1 ? `every ${DOW_LONG[when.days[0]]}` : `every ${daysLabel(when.days)}`;
      return `${days} at ${hm(when.t)}`;
    }
    case "hours":
      return when.n === 1 ? "every hour, on the hour" : `every ${when.n} hours, on the hour`;
    case "minutes":
      return `every ${when.n} minutes`;
    case "monthly":
      return `on the ${ord(when.dom)} of every month at ${hm(when.t)}`;
    case "cron":
      return `on the cron schedule ${when.expr}`;
    case "event":
      return "when the event happens";
  }
}

/** The weekday a "weekly" or dated "once" schedule falls on, in words, for "Every Friday instead". */
export function dayName(day: number): string {
  return DOW_LONG[day];
}

/** "Mon 21 Sep, 09:00". */
export function describeDate(d: Date): string {
  return `${dayDate(d)}, ${hm([d.getHours(), d.getMinutes()])}`;
}

/** A sidebar row's next run: "09:00" today, "Mon 09:00" this week, "21 Sep" later. */
export function nextShort(d: Date, from: Date = new Date()): string {
  const days = Math.round((new Date(d).setHours(0, 0, 0, 0) - new Date(from).setHours(0, 0, 0, 0)) / 864e5);
  const time = hm([d.getHours(), d.getMinutes()]);
  if (days <= 0) return time;
  if (days < 7) return `${DOW[d.getDay()]} ${time}`;
  return `${d.getDate()} ${MON[d.getMonth()]}`;
}
