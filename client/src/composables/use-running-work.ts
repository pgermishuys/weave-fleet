import {
  computed,
  getCurrentInstance,
  onBeforeUnmount,
  reactive,
  readonly,
  shallowRef,
  toValue,
  watch,
  type ComputedRef,
  type MaybeRefOrGetter,
  type Ref,
} from "vue";
import { api } from "@/api/client";
import { onGlobalEvent, onReconnect } from "@/composables/use-signalr-socket";
import { isWorkEvent } from "@/lib/domain-events";
import {
  applyWorkItem,
  isWorkRunning,
  toRunningWorkItem,
  toRunningWorkItems,
  type RunningWorkItem,
  type WorkOutputPage,
} from "@/lib/running-work";

/**
 * Running work, for the UI. Two views of the same items (`@/lib/running-work`):
 *
 * - {@link useRunningWork}: one session's work, running and recently ended. The background strip above the composer
 *   shows it; the Agents tab reads it too. Its items come from the session's stream (the snapshot's `runningWork` and
 *   the `work.*` events, through `applyDomainEvent` in `use-session-stream.ts`, which publishes them here), from the
 *   `work.*` events on the `sessions` topic, and from `GET /api/sessions/{id}/work` when no stream has said yet.
 * - {@link useRunningWorkAcrossSessions}: what runs in every session, for the status bar's counter. Loaded from
 *   `GET /api/work/running` and kept current by the `work.*` events on the `sessions` topic; loaded again after a
 *   reconnect. No polling: the server sends every change on `sessions`.
 *
 * Both act through {@link stopWork} and {@link readWorkOutput}.
 */

/** How long a finished item stays in the strip with its result before it drops off. */
export const FINISHED_VISIBLE_MS = 30_000;

/** How often elapsed times tick. */
const TICK_MS = 1_000;

/** Each session's work as Fleet last said it, so the strip shows at once when you come back to a session. */
const workBySession = reactive<Record<string, RunningWorkItem[]>>({});

/**
 * When this browser saw each item end, by id. A finished item stays visible for {@link FINISHED_VISIBLE_MS} from
 * here, not from the server's `endedAt`, so a server clock that runs ahead or behind can't hide it or pin it.
 */
const endedSeenAt = new Map<string, number>();

/** What runs in every session, by item id (running items only). */
const runningAcross = reactive(new Map<string, RunningWorkItem>());

/** Items being stopped, by id, so their Stop button waits. */
const stopping = reactive(new Set<string>());

let globalListenerInstalled = false;

function noteEnds(previous: readonly RunningWorkItem[] | undefined, next: readonly RunningWorkItem[]): void {
  const now = Date.now();
  for (const item of next) {
    if (isWorkRunning(item) || endedSeenAt.has(item.id)) continue;
    const before = previous?.find((candidate) => candidate.id === item.id);
    // It ended while this browser watched: its result shows from now. Work that had already ended keeps its own end.
    if (before && isWorkRunning(before)) endedSeenAt.set(item.id, now);
  }
}

function setSessionWork(sessionId: string, items: RunningWorkItem[]): void {
  noteEnds(workBySession[sessionId], items);
  workBySession[sessionId] = items;
}

function applyToSession(item: RunningWorkItem): void {
  const current = workBySession[item.sessionId];
  // A session nothing has loaded yet gets its whole list when something asks for it.
  if (!current) return;
  const next = applyWorkItem(current, item);
  if (next !== current) setSessionWork(item.sessionId, next);
}

function applyToAcross(item: RunningWorkItem): void {
  if (isWorkRunning(item)) runningAcross.set(item.id, item);
  else runningAcross.delete(item.id);
}

/** One listener for the whole app: every `work.*` event on `sessions` updates both views. */
function ensureGlobalListener(): void {
  if (globalListenerInstalled) return;
  globalListenerInstalled = true;
  onGlobalEvent("sessions", (event) => {
    if (!isWorkEvent(event)) return;
    const item = toRunningWorkItem(event.payload);
    if (!item) return;
    applyToSession(item);
    applyToAcross(item);
  });
}

/**
 * The session stream's say on a session's work: its snapshot and live events, already reduced. Called by
 * `use-session-stream.ts` whenever its state's `runningWork` changes.
 */
export function publishRunningWork(sessionId: string, items: readonly RunningWorkItem[]): void {
  if (workBySession[sessionId] === items) return;
  setSessionWork(sessionId, items.slice());
  for (const item of items) applyToAcross(item);
}

/** The reason in an error body: Fleet's `{ error }`, or a problem's `detail` or `title`. */
function errorMessage(body: unknown, fallback: string): string {
  if (body && typeof body === "object") {
    for (const key of ["error", "detail", "title"]) {
      const value = (body as Record<string, unknown>)[key];
      if (typeof value === "string" && value.trim().length > 0) return value;
    }
  }
  return fallback;
}

export type StopWorkResult = { ok: true; item: RunningWorkItem | null } | { ok: false; error: string };

/**
 * Stops one item. Fleet answers with the item ended (`cancelled`, or `lost` when the harness no longer had it); it's
 * applied at once, before its event arrives. Refused when the item can't be stopped on its own, has already ended,
 * or its session isn't running.
 */
export async function stopWork(sessionId: string, itemId: string): Promise<StopWorkResult> {
  if (stopping.has(itemId)) return { ok: false, error: "Already stopping." };
  stopping.add(itemId);
  try {
    const { data, error, response } = await api.POST("/api/sessions/{id}/work/{workId}/stop", {
      params: { path: { id: sessionId, workId: itemId } },
    });
    if (!response.ok) {
      return { ok: false, error: errorMessage(error, `It couldn't be stopped (HTTP ${response.status}).`) };
    }
    const item = toRunningWorkItem(data);
    if (item) {
      applyToSession(item);
      applyToAcross(item);
    }
    return { ok: true, item };
  } catch (stopError) {
    return { ok: false, error: stopError instanceof Error ? stopError.message : "It couldn't be stopped." };
  } finally {
    stopping.delete(itemId);
  }
}

export type ReadWorkOutputResult = { ok: true; page: WorkOutputPage } | { ok: false; status: number; error: string };

/**
 * A page of an item's output from byte `offset`. Ask again from `nextOffset`: more may come while it's below `size`,
 * or while the work runs. Only for items with `canReadOutput`.
 */
export async function readWorkOutput(sessionId: string, itemId: string, offset = 0): Promise<ReadWorkOutputResult> {
  try {
    const { data, error, response } = await api.GET("/api/sessions/{id}/work/{workId}/output", {
      params: { path: { id: sessionId, workId: itemId }, query: { offset } },
    });
    if (!response.ok || !data) {
      return { ok: false, status: response.status, error: errorMessage(error, `The output couldn't be read (HTTP ${response.status}).`) };
    }
    return {
      ok: true,
      page: {
        output: data.output ?? "",
        nextOffset: Number(data.nextOffset),
        size: Number(data.size),
        truncated: data.truncated === true,
      },
    };
  } catch (readError) {
    return { ok: false, status: 0, error: readError instanceof Error ? readError.message : "The output couldn't be read." };
  }
}

/** When an ended item's result stops showing, in this browser's time. */
function visibleUntil(item: RunningWorkItem): number {
  const seen = endedSeenAt.get(item.id);
  const ended = seen ?? (item.endedAt ? Date.parse(item.endedAt) : Number.NaN);
  return (Number.isNaN(ended) ? 0 : ended) + FINISHED_VISIBLE_MS;
}

/** Whether an ended item still shows its result at `now`. */
export function isRecentlyEnded(item: RunningWorkItem, now: number): boolean {
  return !isWorkRunning(item) && visibleUntil(item) > now;
}

/**
 * One clock for every elapsed time, so the strip and the status bar's list read the same second. It ticks every
 * second while any component needs it, and rests otherwise.
 */
const clock = shallowRef(Date.now());
let clockUsers = 0;
let clockTimer: ReturnType<typeof setInterval> | null = null;

/** Sets the clock to now, so items that just arrived are measured against the time they came. */
function touchClock(): void {
  clock.value = Date.now();
}

/** Keeps the clock ticking while `active` says so; stops with the component that asked. */
function useTicker(active: () => boolean): void {
  let using = false;

  function release(): void {
    if (!using) return;
    using = false;
    clockUsers -= 1;
    if (clockUsers === 0 && clockTimer !== null) {
      clearInterval(clockTimer);
      clockTimer = null;
    }
  }

  const stopWatch = watch(active, (isActive) => {
    if (isActive && !using) {
      using = true;
      clockUsers += 1;
      touchClock();
      clockTimer ??= setInterval(touchClock, TICK_MS);
    }
    if (!isActive) release();
  }, { immediate: true });

  if (getCurrentInstance()) {
    onBeforeUnmount(() => {
      stopWatch();
      release();
    });
  }
}

export interface UseRunningWorkResult {
  /** Everything Fleet said about the session's work: running, and what ended in the last ten minutes. */
  items: ComputedRef<readonly RunningWorkItem[]>;
  /** What still runs, oldest first. */
  running: ComputedRef<readonly RunningWorkItem[]>;
  /** What ended in the last {@link FINISHED_VISIBLE_MS}, oldest first: shown with its result, then dropped. */
  finished: ComputedRef<readonly RunningWorkItem[]>;
  /** What the strip shows: running, then recently finished. */
  visible: ComputedRef<readonly RunningWorkItem[]>;
  /** The time elapsed times count to; ticks every second while something runs or shows its result. */
  now: Readonly<Ref<number>>;
  /** Whether a Stop for the item is on its way. */
  isStopping: (itemId: string) => boolean;
  /** Stops an item of this session. */
  stop: (itemId: string) => Promise<StopWorkResult>;
  /** A page of an item's output from byte `offset`. */
  readOutput: (itemId: string, offset?: number) => Promise<ReadWorkOutputResult>;
  /** Loads the session's work from Fleet again (`all` for everything it ever ran, as the Agents tab's history needs). */
  refresh: (options?: { all?: boolean }) => Promise<void>;
}

/**
 * One session's running work. The background strip above the composer and the Agents tab read it. Loads the session's
 * work when nothing (its stream) has yet, and again after a reconnect.
 */
export function useRunningWork(sessionId: MaybeRefOrGetter<string | null | undefined>): UseRunningWorkResult {
  ensureGlobalListener();
  const id = computed(() => toValue(sessionId) ?? "");
  let loadId = 0;

  async function refresh(options: { all?: boolean } = {}): Promise<void> {
    const target = id.value;
    if (!target) return;
    const current = ++loadId;
    try {
      const { data, response } = await api.GET("/api/sessions/{id}/work", {
        params: { path: { id: target }, query: options.all ? { all: true } : {} },
      });
      if (current !== loadId || !response.ok || target !== id.value) return;
      let next = toRunningWorkItems(data);
      // Anything the stream said in the meantime is as new as this, or newer.
      for (const item of workBySession[target] ?? []) next = applyWorkItem(next, item);
      setSessionWork(target, next);
    } catch (loadError) {
      console.warn(`Failed to load the running work of session ${target}:`, loadError);
    }
  }

  watch(id, (target) => {
    if (target && !workBySession[target]) void refresh();
  }, { immediate: true });

  const stopReconnect = onReconnect(() => void refresh());
  if (getCurrentInstance()) onBeforeUnmount(stopReconnect);

  const items = computed<readonly RunningWorkItem[]>(() => workBySession[id.value] ?? []);
  const running = computed(() => items.value.filter(isWorkRunning));
  // The clock rests while nothing shows, so new items are measured against the time they arrive.
  watch(items, touchClock, { flush: "sync" });
  const finished = computed(() => items.value.filter((item) => isRecentlyEnded(item, clock.value)));
  const visible = computed(() => [...running.value, ...finished.value]);
  useTicker(() => running.value.length > 0 || finished.value.length > 0);

  return {
    items,
    running,
    finished,
    visible,
    now: readonly(clock),
    isStopping: (itemId) => stopping.has(itemId),
    stop: (itemId) => stopWork(id.value, itemId),
    readOutput: (itemId, offset = 0) => readWorkOutput(id.value, itemId, offset),
    refresh,
  };
}

export interface RunningWorkGroup {
  sessionId: string;
  items: readonly RunningWorkItem[];
}

export interface UseRunningWorkAcrossSessionsResult {
  /** Everything running, in every session, oldest first. */
  running: ComputedRef<readonly RunningWorkItem[]>;
  /** The same, by session, in the order each session's oldest item started. */
  groups: ComputedRef<readonly RunningWorkGroup[]>;
  /** How many sessions have work running. */
  sessionCount: ComputedRef<number>;
  now: Readonly<Ref<number>>;
  isStopping: (itemId: string) => boolean;
  refresh: () => Promise<void>;
}

let acrossLoadId = 0;

async function loadAcross(): Promise<void> {
  const current = ++acrossLoadId;
  try {
    const { data, response } = await api.GET("/api/work/running");
    if (current !== acrossLoadId || !response.ok) return;
    runningAcross.clear();
    for (const item of toRunningWorkItems(data)) if (isWorkRunning(item)) runningAcross.set(item.id, item);
  } catch (loadError) {
    console.warn("Failed to load the running work:", loadError);
  }
}

/** What runs in every session, for the status bar's counter and its popover. */
export function useRunningWorkAcrossSessions(): UseRunningWorkAcrossSessionsResult {
  ensureGlobalListener();
  void loadAcross();
  const stopReconnect = onReconnect(() => void loadAcross());
  if (getCurrentInstance()) onBeforeUnmount(stopReconnect);

  const running = computed<readonly RunningWorkItem[]>(() =>
    [...runningAcross.values()].sort((a, b) => Date.parse(a.startedAt) - Date.parse(b.startedAt) || a.id.localeCompare(b.id)));
  const groups = computed<readonly RunningWorkGroup[]>(() => {
    const bySession = new Map<string, RunningWorkItem[]>();
    for (const item of running.value) {
      const group = bySession.get(item.sessionId);
      if (group) group.push(item);
      else bySession.set(item.sessionId, [item]);
    }
    return [...bySession].map(([sessionId, items]) => ({ sessionId, items }));
  });
  const sessionCount = computed(() => groups.value.length);
  watch(running, touchClock, { flush: "sync" });
  useTicker(() => running.value.length > 0);

  return { running, groups, sessionCount, now: readonly(clock), isStopping: (itemId) => stopping.has(itemId), refresh: loadAcross };
}

/** Forgets everything, between tests. */
export function _resetRunningWorkForTesting(): void {
  for (const key of Object.keys(workBySession)) delete workBySession[key];
  endedSeenAt.clear();
  runningAcross.clear();
  stopping.clear();
  globalListenerInstalled = false;
  if (clockTimer !== null) clearInterval(clockTimer);
  clockTimer = null;
  clockUsers = 0;
  touchClock();
  acrossLoadId = 0;
}
