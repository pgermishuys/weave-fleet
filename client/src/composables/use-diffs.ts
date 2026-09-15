import { computed, readonly, shallowRef, toValue, watch, type ComputedRef, type MaybeRefOrGetter, type Ref, type ShallowRef } from "vue";
import type { FileDiffItem, SessionDiffsResponse } from "@/api/client";
import { api } from "@/api/client";
import { useWeaveSocket } from "@/composables/use-weave-socket";
import type { DomainEvent } from "@/lib/domain-events";

export interface UseDiffsResult {
  /** The changed files with their line counts. Contents aren't included; see {@link fetchFileDiff}. */
  diffs: Readonly<Ref<readonly FileDiffItem[]>>;
  /** The same list by path, for lookups from every row of a file tree. */
  byFile: ComputedRef<ReadonlyMap<string, FileDiffItem>>;
  available: Readonly<ShallowRef<boolean>>;
  isLoading: Readonly<ShallowRef<boolean>>;
  isStale: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | undefined>>;
  fetchDiffs: () => Promise<void>;
  markStale: () => void;
}

function listKey(items: readonly FileDiffItem[]): string {
  return items.map((item) => `${item.file}\u0000${item.status}\u0000${item.additions}\u0000${item.deletions}`).join("\n");
}

export function useDiffs(
  sessionId: MaybeRefOrGetter<string | null | undefined>,
): UseDiffsResult {
  // Replaced whole, never mutated: a session can change hundreds of files.
  const diffs = shallowRef<readonly FileDiffItem[]>([]);
  const available = shallowRef(false);
  const isLoading = shallowRef(false);
  const isStale = shallowRef(false);
  const error = shallowRef<string | undefined>(undefined);
  const currentSessionId = computed(() => toValue(sessionId) ?? "");
  const { subscribeV2 } = useWeaveSocket();

  let requestId = 0;
  let debounceTimeoutId: ReturnType<typeof setTimeout> | undefined;

  async function fetchDiffs(): Promise<void> {
    const activeSessionId = currentSessionId.value;

    if (!activeSessionId) {
      requestId += 1;
      diffs.value = [];
      available.value = false;
      isLoading.value = false;
      isStale.value = false;
      error.value = undefined;
      return;
    }

    const currentRequestId = ++requestId;
    isLoading.value = true;
    error.value = undefined;

    try {
      const { data, error: apiError, response } = await api.GET("/api/sessions/{id}/diffs", {
        params: {
          path: { id: activeSessionId },
        },
      });

      if (apiError || !response.ok) {
        throw new Error(`HTTP ${response.status}`);
      }

      if (currentRequestId !== requestId) {
        return;
      }

      // Response body is not typed in schema, use data from openapi-fetch
      const responseData = data as unknown as SessionDiffsResponse | FileDiffItem[] | undefined;

      // API returns { diffs: [...], available: boolean } wrapper object.
      const items: readonly FileDiffItem[] = Array.isArray(responseData) ? responseData : Array.isArray(responseData?.diffs) ? responseData.diffs : [];
      // Keep the old list when nothing changed, so what's derived from it doesn't recompute on every edit event.
      if (listKey(items) !== listKey(diffs.value)) diffs.value = items;
      available.value = Array.isArray(responseData) || typeof responseData?.available !== "boolean" ? true : responseData.available;
      isStale.value = false;
      error.value = undefined;
    } catch (fetchError) {
      if (currentRequestId !== requestId) {
        return;
      }

      available.value = false;
      error.value = fetchError instanceof Error ? fetchError.message : String(fetchError);
    } finally {
      if (currentRequestId === requestId) {
        isLoading.value = false;
      }
    }
  }

  function markStale(): void {
    if (!currentSessionId.value) {
      return;
    }

    isStale.value = true;
  }

  function debouncedFetchDiffs(): void {
    if (debounceTimeoutId) {
      clearTimeout(debounceTimeoutId);
    }

    debounceTimeoutId = setTimeout(() => {
      void fetchDiffs();
      debounceTimeoutId = undefined;
    }, 500);
  }

  watch(
    currentSessionId,
    () => {
      requestId += 1;
      diffs.value = [];
      available.value = false;
      isLoading.value = false;
      isStale.value = false;
      error.value = undefined;
    },
    { immediate: true },
  );

  watch(
    currentSessionId,
    (activeSessionId, _previousSessionId, onCleanup) => {
      if (!activeSessionId) {
        return;
      }

      const unsubscribe = subscribeV2(
        `session:${activeSessionId}`,
        () => {
          // Diff state is loaded from the REST endpoint; snapshots are ignored here.
        },
        (event: DomainEvent) => {
          if (event.type === "turn.ended" && event.payload.sessionID === activeSessionId) {
            void fetchDiffs();
          } else if (event.type === "files.changed" && event.payload.sessionId === activeSessionId) {
            debouncedFetchDiffs();
          }
        },
      );

      onCleanup(() => {
        if (debounceTimeoutId) {
          clearTimeout(debounceTimeoutId);
          debounceTimeoutId = undefined;
        }
        unsubscribe();
      });
    },
    { immediate: true },
  );

  const byFile = computed(() => new Map(diffs.value.map((diff) => [diff.file, diff])));

  return {
    diffs: computed(() => diffs.value),
    byFile,
    available: readonly(available),
    isLoading: readonly(isLoading),
    isStale: readonly(isStale),
    error: readonly(error),
    fetchDiffs,
    markStale,
  };
}
