import { storeToRefs } from "pinia";
import { computed, shallowRef, toValue, watch, type ComputedRef, type MaybeRefOrGetter } from "vue";
import type { SessionListItem } from "@/api/client";
import { liveTarget, type MachineTarget } from "@/lib/machine-target";
import { useSessionsStore } from "@/stores/sessions";

/** How long the archived and hidden sessions fetched for the `@` list are reused before they're asked for again. */
const REFRESH_MS = 30_000;
const FETCH_LIMIT = 200;

/** What each machine's `@` lists fetched, by its key. */
const fetchedOn = new Map<string, { sessions: readonly SessionListItem[]; at: number; pending: Promise<void> | null }>();

/** For tests: forgets the sessions fetched for the `@` list. */
export function resetReferableSessions(): void {
  fetchedOn.clear();
}

/**
 * The sessions the composer's `@` list can offer: the ones the session list holds, and, fetched when the list first
 * opens, the archived ones it doesn't. What the session list holds wins, since its status is live. Another machine's
 * (the New Session page's Machine chip) are all fetched from it, since the session list holds only the live machine's.
 */
export function useReferableSessions(
  active: MaybeRefOrGetter<boolean>,
  target: MachineTarget = liveTarget(),
): ComputedRef<readonly SessionListItem[]> {
  const { sessions } = storeToRefs(useSessionsStore());
  const fetched = () => {
    let entry = fetchedOn.get(target.key);
    if (!entry) {
      entry = { sessions: [], at: 0, pending: null };
      fetchedOn.set(target.key, entry);
    }
    return entry;
  };
  const more = shallowRef<readonly SessionListItem[]>(fetched().sessions);

  async function load(): Promise<void> {
    const entry = fetched();
    if (Date.now() - entry.at < REFRESH_MS) {
      more.value = entry.sessions;
      return;
    }

    entry.pending ??= (async () => {
      try {
        const { data, response } = await target.api.GET("/api/sessions", {
          params: { query: { retentionStatus: "all", limit: FETCH_LIMIT } },
        });
        if (response.ok && Array.isArray(data)) {
          entry.sessions = data as unknown as SessionListItem[];
          entry.at = Date.now();
        }
      } catch {
        // The session list's own sessions are still offered.
      } finally {
        entry.pending = null;
      }
    })();
    await entry.pending;
    more.value = entry.sessions;
  }

  watch(() => toValue(active), (isActive) => {
    if (isActive) void load();
  }, { immediate: true });

  return computed(() => {
    const listed = target.isLive ? sessions.value : [];
    const known = new Set(listed.map((item) => item.session.id));
    return [...listed, ...more.value.filter((item) => !known.has(item.session.id))];
  });
}
