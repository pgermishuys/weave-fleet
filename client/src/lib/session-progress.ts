/**
 * How far along a session is, as the server works it out from the session's events. The server pushes
 * the row summary on the "sessions" topic and the full detail on the session's own topic.
 */
import { normalizeTodoItem, type TodoItem } from "@/lib/todo-utils";

/** Pushed on the "sessions" topic with a {@link SessionProgressSummary}. */
export const SESSION_PROGRESS = "session_progress";

/** Pushed on a session's own topic with a {@link SessionProgressDetail}. */
export const PROGRESS_UPDATED = "progress.updated";

/** What a session row shows. */
export interface SessionProgressSummary {
  sessionId: string;
  /** What the counts count: "todos" today. */
  kind: string;
  done: number;
  total: number;
  /** The item being worked on, or the next one. */
  current?: string | null;
}

/** One checkbox step of a plan. */
export interface SessionPlanStep {
  key: string;
  number: string | null;
  title: string;
  checked: boolean;
  subDone: number;
  subTotal: number;
  /** When Fleet saw the box ticked; null if before it was watching, or not ticked. */
  tickedAt: string | null;
  tickedInMessageId: string | null;
}

export interface SessionPlanGroup {
  title: string | null;
  steps: SessionPlanStep[];
}

/** A checklist plan the session is working through. */
export interface SessionPlan {
  /** Relative to the session's folder. */
  path: string;
  title: string | null;
  /** When Fleet first read the file. */
  trackedSince: string;
  groups: SessionPlanGroup[];
}

/** Everything the open session shows. */
export interface SessionProgressDetail extends SessionProgressSummary {
  todos: TodoItem[];
  updatedAt: string;
  /** The plan the counts come from, when the session is working through one. */
  plan: SessionPlan | null;
}

function asRecord(value: unknown): Record<string, unknown> | null {
  return value !== null && typeof value === "object" && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : null;
}

function isCount(value: unknown): value is number {
  return typeof value === "number" && Number.isInteger(value) && value >= 0;
}

export function parseProgressSummary(value: unknown): SessionProgressSummary | null {
  const record = asRecord(value);
  if (
    !record
    || typeof record.sessionId !== "string"
    || record.sessionId === ""
    || typeof record.kind !== "string"
    || !isCount(record.done)
    || !isCount(record.total)
  ) {
    return null;
  }

  return {
    sessionId: record.sessionId,
    kind: record.kind,
    done: record.done,
    total: record.total,
    current: typeof record.current === "string" ? record.current : null,
  };
}

export function parseProgressDetail(value: unknown): SessionProgressDetail | null {
  const summary = parseProgressSummary(value);
  const record = asRecord(value);
  if (!summary || !record || typeof record.updatedAt !== "string") {
    return null;
  }

  const todos = Array.isArray(record.todos)
    ? record.todos.map(normalizeTodoItem).filter((todo): todo is TodoItem => todo !== null)
    : [];

  return { ...summary, todos, updatedAt: record.updatedAt, plan: parsePlan(record.plan) };
}

const stringOrNull = (value: unknown): string | null => (typeof value === "string" ? value : null);

function parseStep(value: unknown): SessionPlanStep | null {
  const record = asRecord(value);
  if (!record || typeof record.key !== "string" || typeof record.title !== "string") return null;
  return {
    key: record.key,
    number: stringOrNull(record.number),
    title: record.title,
    checked: record.checked === true,
    subDone: isCount(record.subDone) ? record.subDone : 0,
    subTotal: isCount(record.subTotal) ? record.subTotal : 0,
    tickedAt: stringOrNull(record.tickedAt),
    tickedInMessageId: stringOrNull(record.tickedInMessageId),
  };
}

function parsePlan(value: unknown): SessionPlan | null {
  const record = asRecord(value);
  if (!record || typeof record.path !== "string" || !Array.isArray(record.groups)) return null;

  const groups = record.groups
    .map((group): SessionPlanGroup | null => {
      const groupRecord = asRecord(group);
      if (!groupRecord || !Array.isArray(groupRecord.steps)) return null;
      const steps = groupRecord.steps.map(parseStep).filter((step): step is SessionPlanStep => step !== null);
      return steps.length > 0 ? { title: stringOrNull(groupRecord.title), steps } : null;
    })
    .filter((group): group is SessionPlanGroup => group !== null);

  return groups.length > 0
    ? { path: record.path, title: stringOrNull(record.title), trackedSince: stringOrNull(record.trackedSince) ?? "", groups }
    : null;
}

/** The step being worked on next: the first unticked one. */
export function currentPlanStep(plan: SessionPlan): { group: SessionPlanGroup; step: SessionPlanStep } | null {
  for (const group of plan.groups) {
    const step = group.steps.find((candidate) => !candidate.checked);
    if (step) return { group, step };
  }
  return null;
}

/** Splits text on backtick code spans, so a template can render `code` without v-html. */
export function codeSpans(text: string): Array<{ text: string; code: boolean }> {
  return text
    .split(/(`[^`]+`)/)
    .filter((piece) => piece.length > 0)
    .map((piece) => (piece.length > 2 && piece.startsWith("`") && piece.endsWith("`")
      ? { text: piece.slice(1, -1), code: true }
      : { text: piece, code: false }));
}

/** The text without backticks, for places that can't show code, like a one-line strip. */
export function plainText(text: string): string {
  return text.replace(/`([^`]+)`/g, "$1");
}

/** True when two summaries would draw the same row. */
export function sameProgressSummary(
  a: SessionProgressSummary | null | undefined,
  b: SessionProgressSummary | null | undefined,
): boolean {
  if (!a || !b) return !a && !b;
  return a.kind === b.kind && a.done === b.done && a.total === b.total && (a.current ?? null) === (b.current ?? null);
}
