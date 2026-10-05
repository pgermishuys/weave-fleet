/**
 * Running work: what a session's agent left running (background shells, subagents, monitors, tasks), as Fleet
 * records it for every harness. One item per piece of work, the same shape in the snapshot's `runningWork`, the
 * `work.started` / `work.updated` / `work.ended` events (on `session:{id}` and on `sessions`), and the REST routes
 * (`GET /api/sessions/{id}/work`, `GET /api/work/running`). See the API section of
 * `docs/background-work-and-lineage.md`.
 *
 * Every event carries the whole item as it is now, so applying one is a replace by `id`. The UI never looks at harness
 * specifics: what a row offers comes from `canStop`, `canReadOutput` and `childSessionId`.
 */

export type RunningWorkKind = "subagent" | "shell" | "monitor" | "task";
export type RunningWorkStatus = "pending" | "running" | "completed" | "error" | "cancelled";
export type RunningWorkEndedReason = "completed" | "error" | "cancelled" | "lost";

export interface RunningWorkItem {
  /** Fleet's id for the item; stop and output take this. */
  id: string;
  /** The session whose agent started it. */
  sessionId: string;
  /** The harness's own handle (a shell id, a task id, a subagent's call id). */
  workId: string;
  kind: RunningWorkKind;
  /** Its short name: the subagent's agent, or the tool that started it. */
  title: string;
  /** What it is: the subagent's task, the shell's command. */
  label: string | null;
  status: RunningWorkStatus;
  /** The call that started it returned while the work runs on. */
  background: boolean;
  /** The Fleet session it runs in (subagents), when it has one. */
  childSessionId: string | null;
  /** The tool call that started it: its card in the conversation. */
  toolCallId: string | null;
  canStop: boolean;
  canReadOutput: boolean;
  startedAt: string;
  endedAt: string | null;
  endedReason: RunningWorkEndedReason | null;
  /** Its result in a few words, e.g. `exit 0`. */
  detail: string | null;
}

/** A page of a work item's output (`GET /api/sessions/{id}/work/{workId}/output`). Offsets count bytes. */
export interface WorkOutputPage {
  output: string;
  /** Where to ask from next. More may come while it's below `size`, or while the work runs. */
  nextOffset: number;
  size: number;
  /** The harness kept only part of the output; what's before it is gone. */
  truncated: boolean;
}

const KINDS: readonly RunningWorkKind[] = ["subagent", "shell", "monitor", "task"];
const STATUSES: readonly RunningWorkStatus[] = ["pending", "running", "completed", "error", "cancelled"];
const ENDED_REASONS: readonly RunningWorkEndedReason[] = ["completed", "error", "cancelled", "lost"];

function optionalString(value: unknown): string | null {
  return typeof value === "string" && value.length > 0 ? value : null;
}

/**
 * The item a payload describes, or null when it isn't one. The server leaves nulls (and false) out of the JSON, so
 * missing fields get their empty values here; a kind or status this client doesn't know reads as a task, running.
 */
export function toRunningWorkItem(value: unknown): RunningWorkItem | null {
  if (!value || typeof value !== "object") return null;
  const raw = value as Record<string, unknown>;
  const { id, sessionId, workId } = raw;
  if (typeof id !== "string" || !id || typeof sessionId !== "string" || !sessionId) return null;

  const kind = KINDS.includes(raw.kind as RunningWorkKind) ? (raw.kind as RunningWorkKind) : "task";
  const status = STATUSES.includes(raw.status as RunningWorkStatus) ? (raw.status as RunningWorkStatus) : "running";
  const endedReason = ENDED_REASONS.includes(raw.endedReason as RunningWorkEndedReason)
    ? (raw.endedReason as RunningWorkEndedReason)
    : null;

  return {
    id,
    sessionId,
    workId: typeof workId === "string" ? workId : "",
    kind,
    title: typeof raw.title === "string" ? raw.title : kind,
    label: optionalString(raw.label),
    status,
    background: raw.background === true,
    childSessionId: optionalString(raw.childSessionId),
    toolCallId: optionalString(raw.toolCallId),
    canStop: raw.canStop === true,
    canReadOutput: raw.canReadOutput === true,
    startedAt: typeof raw.startedAt === "string" ? raw.startedAt : new Date(0).toISOString(),
    endedAt: optionalString(raw.endedAt),
    endedReason,
    detail: optionalString(raw.detail),
  };
}

/** The items a list payload holds; anything that isn't one is dropped. */
export function toRunningWorkItems(values: unknown): RunningWorkItem[] {
  if (!Array.isArray(values)) return [];
  return sortWork(values.map(toRunningWorkItem).filter((item): item is RunningWorkItem => item !== null));
}

/** Whether the work is still going: pending or running, and not ended. */
export function isWorkRunning(item: Pick<RunningWorkItem, "status" | "endedAt">): boolean {
  return (item.status === "pending" || item.status === "running") && !item.endedAt;
}

function sortWork(items: RunningWorkItem[]): RunningWorkItem[] {
  return items.sort((a, b) => Date.parse(a.startedAt) - Date.parse(b.startedAt) || a.id.localeCompare(b.id));
}

/**
 * The list with `item` in it, replacing the item with its id; the same list when nothing changed. Work that ended
 * stays ended: a late event that still says running (one sent before the end, delivered after it) doesn't bring it
 * back, as on the server.
 */
export function applyWorkItem(items: readonly RunningWorkItem[], item: RunningWorkItem): RunningWorkItem[] {
  const index = items.findIndex((candidate) => candidate.id === item.id);
  if (index === -1) return sortWork([...items, item]);

  const existing = items[index];
  if (!isWorkRunning(existing) && isWorkRunning(item)) return items as RunningWorkItem[];
  if (sameItem(existing, item)) return items as RunningWorkItem[];

  const next = items.slice();
  next[index] = item;
  return next;
}

function sameItem(a: RunningWorkItem, b: RunningWorkItem): boolean {
  return (Object.keys(a) as (keyof RunningWorkItem)[]).every((key) => a[key] === b[key]);
}

/** What the row calls the kind: Shell, Subagent, Monitor, Task. */
export function workKindLabel(kind: RunningWorkKind): string {
  switch (kind) {
    case "subagent": return "Subagent";
    case "shell": return "Shell";
    case "monitor": return "Monitor";
    default: return "Task";
  }
}

/**
 * A short duration: `58s` in the first minute, then whole minutes, `4m`, `1h 12m`. Seconds only while they matter, so
 * a busy screen doesn't count them in several places at once; the tooltip keeps the exact start.
 */
export function formatElapsed(ms: number): string {
  const seconds = Math.max(0, Math.floor(ms / 1000));
  if (seconds < 60) return `${seconds}s`;
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes}m`;
  const hours = Math.floor(minutes / 60);
  return minutes % 60 === 0 ? `${hours}h` : `${hours}h ${minutes % 60}m`;
}

/** How long ago something ended: `just now` in the first minute, then `4m ago`. */
export function formatAgo(ms: number): string {
  return ms < 60_000 ? "just now" : `${formatElapsed(ms)} ago`;
}

/** How often an elapsed time of `ms` needs to tick to stay right: every second in its first minute, then every minute. */
export function elapsedTickMs(ms: number): number {
  return ms < 60_000 ? 1_000 : 60_000;
}

/** How long it has run, or ran: from its start to its end, or to `now`. */
export function workElapsedMs(item: RunningWorkItem, now: number): number {
  const started = Date.parse(item.startedAt);
  const ended = item.endedAt ? Date.parse(item.endedAt) : now;
  return Number.isNaN(started) ? 0 : Math.max(0, (Number.isNaN(ended) ? now : ended) - started);
}

/** How the work ended, in a few words: its own detail (`exit 0`), else what ended it. Null while it runs. */
export function workResult(item: RunningWorkItem): string | null {
  if (isWorkRunning(item)) return null;
  if (item.detail) return item.detail;
  switch (item.endedReason ?? item.status) {
    case "error": return "failed";
    case "cancelled": return "stopped";
    case "lost": return "lost";
    default: return "done";
  }
}

/** Whether the work ended badly: failed, or lost when the harness went away. Stopped and finished aren't. */
export function workFailed(item: RunningWorkItem): boolean {
  return !isWorkRunning(item) && (item.status === "error" || item.endedReason === "error" || item.endedReason === "lost");
}
