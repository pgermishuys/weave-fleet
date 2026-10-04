import { computed, reactive, toValue, watch, type ComputedRef, type MaybeRefOrGetter } from "vue";
import type { SessionListItem } from "@/api/client";
import { useRunningWork, type UseRunningWorkResult } from "@/composables/use-running-work";
import { apiFetch } from "@/lib/api-client";
import { applyWorkItem, type RunningWorkItem } from "@/lib/running-work";
import { buildAgentsLineage, lineageOf, type AgentsLineage, type LineageLink } from "@/lib/session-lineage";
import { useSessionsStore } from "@/stores/sessions";

/**
 * A session's lineage for the UI: the session it came from, and the agents and sessions it started (the Agents tab).
 * Its work comes from {@link useRunningWork}; what ended long ago comes from the same composable's `refresh({ all })`,
 * and is kept here once seen, since the session's stream only says what ended in the last ten minutes.
 */

/** Titles of sessions the list doesn't hold (archived, a subagent's hidden session), by id, once fetched. */
const fetchedTitles = reactive<Record<string, string | null>>({});
const fetching = new Set<string>();

async function fetchTitle(sessionId: string): Promise<void> {
  if (fetching.has(sessionId) || sessionId in fetchedTitles) return;
  fetching.add(sessionId);
  try {
    const response = await apiFetch(`/api/sessions/${encodeURIComponent(sessionId)}`);
    const body = response.ok ? (await response.json()) as { title?: string | null } : null;
    fetchedTitles[sessionId] = body?.title?.trim() || null;
  } catch {
    fetchedTitles[sessionId] = null;
  } finally {
    fetching.delete(sessionId);
  }
}

/** A session's title: from the list, else fetched once. Null while unknown. */
export function useSessionTitle(sessionId: MaybeRefOrGetter<string | null | undefined>): ComputedRef<string | null> {
  const sessionsStore = useSessionsStore();
  const listed = computed(() => {
    const id = toValue(sessionId);
    return id ? sessionsStore.sessions.find((item) => item.session.id === id) ?? null : null;
  });
  watch(() => toValue(sessionId), (id) => {
    if (id && !listed.value) void fetchTitle(id);
  }, { immediate: true });
  return computed(() => {
    const id = toValue(sessionId);
    if (!id) return null;
    return listed.value?.session.title?.trim() || fetchedTitles[id] || null;
  });
}

/** Ended work seen for each session, by item id, so the Agents tab keeps its earlier agents. */
const historyBySession = reactive<Record<string, RunningWorkItem[]>>({});

function remember(sessionId: string, items: readonly RunningWorkItem[]): void {
  let next = historyBySession[sessionId] ?? [];
  for (const item of items) next = applyWorkItem(next, item);
  if (next !== historyBySession[sessionId]) historyBySession[sessionId] = next;
}

export interface UseSessionLineageResult {
  /** The session this one came from, and how. */
  parent: ComputedRef<LineageLink | null>;
  /** The parent's list item, when the list holds it. */
  parentSession: ComputedRef<SessionListItem | null>;
  parentTitle: ComputedRef<string | null>;
  agents: ComputedRef<AgentsLineage>;
  /** Agents working now or waiting on you: the tab's count. */
  activeCount: ComputedRef<number>;
  /** Whether there's anything for the Agents tab to show. */
  hasLineage: ComputedRef<boolean>;
  /** Loads everything the session's agent ever ran, for Earlier agents. */
  loadHistory: () => Promise<void>;
  work: UseRunningWorkResult;
}

export function useSessionLineage(sessionId: MaybeRefOrGetter<string | null | undefined>): UseSessionLineageResult {
  const sessionsStore = useSessionsStore();
  const id = computed(() => toValue(sessionId) ?? "");
  const work = useRunningWork(id);

  watch(work.items, (items) => {
    if (id.value) remember(id.value, items);
  }, { immediate: true });

  const self = computed(() => sessionsStore.sessions.find((item) => item.session.id === id.value) ?? null);
  const parent = computed(() => (self.value ? lineageOf(self.value) : null));
  const parentSession = computed(() => {
    const parentId = parent.value?.parentId;
    return parentId ? sessionsStore.sessions.find((item) => item.session.id === parentId) ?? null : null;
  });
  const parentTitle = useSessionTitle(() => parent.value?.parentId);

  const agents = computed(() => {
    // What the stream says now wins over what was remembered.
    let items = historyBySession[id.value] ?? [];
    for (const item of work.items.value) items = applyWorkItem(items, item);
    return buildAgentsLineage(id.value, sessionsStore.sessions, items);
  });
  const activeCount = computed(() => agents.value.running.length);
  const hasLineage = computed(() => {
    const { running, started, earlier } = agents.value;
    return parent.value !== null || running.length + started.length + earlier.length > 0;
  });

  return {
    parent,
    parentSession,
    parentTitle,
    agents,
    activeCount,
    hasLineage,
    loadHistory: () => work.refresh({ all: true }),
    work,
  };
}

/** Forgets fetched titles and remembered history, between tests. */
export function _resetSessionLineageForTesting(): void {
  for (const key of Object.keys(fetchedTitles)) delete fetchedTitles[key];
  for (const key of Object.keys(historyBySession)) delete historyBySession[key];
  fetching.clear();
}
