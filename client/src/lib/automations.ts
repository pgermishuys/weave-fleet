import {
  dayName,
  describeDate,
  nextRun,
  nextShort,
  whenSentence,
  type ScheduleHit,
  type When,
} from "@/lib/automation-schedule";
import { tildePath, type PlanPart } from "@/lib/new-session-plan";
import type { NewSessionFolder } from "@/lib/new-session-request";
import type { Automation, AutomationRun } from "@/stores/automations";

/** What each event an automation can wait for means, in the words the form and page use. */
const EVENT_LABELS: Record<string, string> = {
  session_created: "A session starts",
  session_archived: "A session is archived",
  session_deleted: "A session is deleted",
  "delegation.created": "A sub agent starts",
  "delegation.updated": "A sub agent's status changes",
};

/** The event types an automation can wait for (the server's catalog is the same list). */
export const EVENT_TYPES: readonly string[] = Object.keys(EVENT_LABELS);

/** An event type in words; one the server no longer sends is marked, since that trigger can't fire. */
export function describeEventType(eventType: string): string {
  return EVENT_LABELS[eventType] ?? `${eventType} (never fires)`;
}

/** The event type an event trigger waits for, from its stored config ({"eventType": …}). */
export function eventTypeOf(triggerConfig: string): string {
  try {
    const parsed = JSON.parse(triggerConfig) as { eventType?: unknown };
    return typeof parsed.eventType === "string" ? parsed.eventType : triggerConfig;
  } catch {
    return triggerConfig;
  }
}

/** The browser's IANA time zone, which new and edited schedules are saved in. */
export function browserTimeZone(): string | null {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone || null;
  } catch {
    return null;
  }
}

/** The zone a saved schedule runs in, as the page says it: automations saved without one run in UTC. */
export function scheduleTimeZone(timeZone: string | null | undefined): string {
  return timeZone || "UTC";
}

// ── The line under the composer ────────────────────────────────────────────────

export interface AutomationPlanInput {
  /** What's in the box. */
  text: string;
  when: When | null;
  /** The schedule words found in the text; null when the When menu chose instead. */
  hit: ScheduleHit | null;
  /** What the agent will be asked. */
  prompt: string;
  /** The sentence said "on Friday" and "Just once" was chosen. */
  forceOnce: boolean;
  folder: NewSessionFolder | null;
  /** Each run gets a new worktree of the folder. */
  worktree: boolean;
  /** Where a worktree starts, once known. */
  base: string | null;
  sameSession: boolean;
  /** The zone the schedule will be saved in (the browser's). */
  timeZone: string;
  /** An existing automation's zone, to say when saving moves it; undefined for a new one. */
  savedTimeZone?: string | null;
  /** An automation made before folders were kept per run: no folder means the first workspace root. */
  legacyFolderless?: boolean;
  from?: Date;
}

export interface AutomationPlan {
  /** When it runs. */
  schedule: PlanPart[];
  /** A question the schedule leaves open, answered by a button after it. */
  ask: { question: string; answer: string; action: "just-once" | "every-week" } | null;
  /** Where each run happens and what the agent gets. */
  rest: PlanPart[];
}

/** One sentence (or three) saying what pressing Enter will make. */
export function describeAutomationPlan(input: AutomationPlanInput): AutomationPlan {
  const { when, folder } = input;
  const schedule: PlanPart[] = [];
  let ask: AutomationPlan["ask"] = null;

  if (!when) {
    schedule.push({
      text: input.text.trim()
        ? "When should it run? Add it to the message (“every Monday at 9”) or pick it under When."
        : "Say what it should do and when, anywhere in the message, e.g. “every Monday at 9”.",
    });
  } else if (when.kind === "event") {
    schedule.push({ text: "Runs when " }, { text: describeEventType(when.eventType).replace(/^A /, "a ") + "." });
  } else {
    const next = when.kind === "once" ? null : nextRun(when, input.from);
    schedule.push(
      { text: "Runs " },
      { text: whenSentence(when, input.from), strong: true },
      { text: ` (${input.timeZone} time)` },
      ...(next ? [{ text: ", next " }, { text: describeDate(next), strong: true }] : []),
      { text: when.kind === "once" ? ", then it switches off." : "." },
    );
    if (input.savedTimeZone !== undefined && (input.savedTimeZone ?? "UTC") !== input.timeZone) {
      schedule.push({ text: ` Saving moves it from ${input.savedTimeZone ?? "UTC"} time to ${input.timeZone}.`, warn: true });
    }
    if (when.kind === "weekly" && when.ambiguous && !input.forceOnce && input.hit) {
      ask = { question: `Every ${dayName(when.days[0])}, or just once?`, answer: "Just once", action: "just-once" };
    } else if (input.forceOnce && input.hit?.when.kind === "weekly" && input.hit.when.ambiguous) {
      ask = { question: "", answer: `Every ${dayName(input.hit.when.days[0])} instead`, action: "every-week" };
    }
  }

  const rest: PlanPart[] = [];
  const session = input.sameSession ? "session" : "new session";
  if (!folder) {
    rest.push({ text: "Choose where it runs." });
  } else if (folder.kind === "none") {
    rest.push({ text: input.legacyFolderless ? `Each run: a ${session} in your first workspace root.` : `Each run: a ${session} with no folder.` });
  } else if (folder.kind === "repository" && input.worktree) {
    rest.push(
      { text: `Each run: a ${session} in a new worktree of ` },
      { text: tildePath(folder.path), code: true },
      ...(input.base ? [{ text: " from " }, { text: input.base, code: true }] : []),
      { text: "." },
    );
  } else {
    rest.push({ text: `Each run: a ${session} in ` }, { text: tildePath(folder.path), code: true }, { text: " as it is." });
  }
  if (input.sameSession) {
    rest.push({ text: " Later runs continue the first run's session." });
  }

  if (input.hit || /automation/i.test(input.text)) {
    rest.push(input.prompt
      ? { text: ` The agent gets “${input.prompt.length > 110 ? `${input.prompt.slice(0, 108)}…` : input.prompt}”` }
      : { text: " Say what it should do.", warn: true });
  }

  return { schedule, ask, rest };
}

// ── Runs and rows ──────────────────────────────────────────────────────────────

/** What started a run, for the runs list; nothing for the schedule itself. */
export function describeRunTrigger(trigger: string): string {
  switch (trigger) {
    case "schedule":
      return "";
    case "catch_up":
      return "Caught up";
    case "once":
      return "Once";
    case "manual":
      return "Run now";
    default:
      return describeEventType(trigger);
  }
}

export type RowTone = "working" | "error" | "warn" | "quiet";

/** A run's state in a word, and how it's coloured. */
export function describeRunState(run: Pick<AutomationRun, "state">): { label: string; tone: RowTone } {
  switch (run.state) {
    case "starting":
      return { label: "Starting", tone: "working" };
    case "running":
      return { label: "Running", tone: "working" };
    case "failed":
      return { label: "Failed", tone: "error" };
    case "skipped":
      return { label: "Skipped", tone: "warn" };
    default:
      return { label: "Done", tone: "quiet" };
  }
}

/**
 * A sidebar row's status, like a session row's: Running while its latest run is going, Failed when that run
 * failed, otherwise when it runs next. Off rows say so and dim.
 */
export function automationRowStatus(automation: Automation, from: Date = new Date()): { label: string; tone: RowTone; glyph: "working" | "error" | null } {
  const last = automation.lastRun;
  if (last && (last.state === "running" || last.state === "starting")) {
    return { label: "Running", tone: "working", glyph: "working" };
  }
  if (!automation.isEnabled) {
    return { label: "Off", tone: "quiet", glyph: null };
  }
  if (last?.state === "failed") {
    return { label: "Failed", tone: "error", glyph: "error" };
  }
  if (automation.nextRunAt) {
    return { label: nextShort(new Date(automation.nextRunAt), from), tone: "quiet", glyph: null };
  }
  return { label: automation.triggerType === "event" ? "On event" : "", tone: "quiet", glyph: null };
}
