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

/** Everything the open session shows. */
export interface SessionProgressDetail extends SessionProgressSummary {
  todos: TodoItem[];
  updatedAt: string;
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

  return { ...summary, todos, updatedAt: record.updatedAt };
}

/** True when two summaries would draw the same row. */
export function sameProgressSummary(
  a: SessionProgressSummary | null | undefined,
  b: SessionProgressSummary | null | undefined,
): boolean {
  if (!a || !b) return !a && !b;
  return a.kind === b.kind && a.done === b.done && a.total === b.total && (a.current ?? null) === (b.current ?? null);
}
