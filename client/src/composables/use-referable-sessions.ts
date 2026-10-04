import { storeToRefs } from "pinia";
import { computed, shallowRef, toValue, watch, type ComputedRef, type MaybeRefOrGetter } from "vue";
import { api, type SessionListItem } from "@/api/client";
import { useSessionsStore } from "@/stores/sessions";

/** How long the archived and hidden sessions fetched for the `@` list are reused before they're asked for again. */
const REFRESH_MS = 30_000;
const FETCH_LIMIT = 200;

let fetched: readonly SessionListItem[] = [];
let fetchedAt = 0;
let pending: Promise<void> | null = null;

/** For tests: forgets the sessions fetched for the `@` list. */
export function resetReferableSessions(): void {
  fetched = [];
  fetchedAt = 0;
  pending = null;
}

/**
 * The sessions the composer's `@` list can offer: the ones the session list holds, and, fetched when the list first
 * opens, the archived ones it doesn't. What the session list holds wins, since its status is live.
 */
export function useReferableSessions(active: MaybeRefOrGetter<boolean>): ComputedRef<readonly SessionListItem[]> {
  const { sessions } = storeToRefs(useSessionsStore());
  const more = shallowRef<readonly SessionListItem[]>(fetched);

  async function load(): Promise<void> {
    if (Date.now() - fetchedAt < REFRESH_MS) {
      more.value = fetched;
      return;
    }

    pending ??= (async () => {
      try {
        const { data, response } = await api.GET("/api/sessions", {
          params: { query: { retentionStatus: "all", limit: FETCH_LIMIT } },
        });
        if (response.ok && Array.isArray(data)) {
          fetched = data as unknown as SessionListItem[];
          fetchedAt = Date.now();
        }
      } catch {
        // The session list's own sessions are still offered.
      } finally {
        pending = null;
      }
    })();
    await pending;
    more.value = fetched;
  }

  watch(() => toValue(active), (isActive) => {
    if (isActive) void load();
  }, { immediate: true });

  return computed(() => {
    const known = new Set(sessions.value.map((item) => item.session.id));
    return [...sessions.value, ...more.value.filter((item) => !known.has(item.session.id))];
  });
}
