import { computed, readonly, ref, shallowRef, toValue, watch, type MaybeRefOrGetter, type Ref, type ShallowRef } from "vue";
import type { FileDiffItem, SessionDiffsResponse } from "@/api/client";
import { api } from "@/api/client";
import { useWeaveSocket } from "@/composables/use-weave-socket";
import type { DomainEvent } from "@/lib/domain-events";

export interface UseDiffsResult {
  diffs: Readonly<Ref<readonly FileDiffItem[]>>;
  available: Readonly<ShallowRef<boolean>>;
  isLoading: Readonly<ShallowRef<boolean>>;
  isStale: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | undefined>>;
  fetchDiffs: () => Promise<void>;
  markStale: () => void;
}

export function useDiffs(
  sessionId: MaybeRefOrGetter<string | null | undefined>,
): UseDiffsResult {
  const diffs = ref<FileDiffItem[]>([]);
  const available = shallowRef(false);
  const isLoading = shallowRef(false);
  const isStale = shallowRef(false);
  const error = shallowRef<string | undefined>(undefined);
  const currentSessionId = computed(() => toValue(sessionId) ?? "");
  const { subscribeV2 } = useWeaveSocket();

  let requestId = 0;

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
      const items = Array.isArray(responseData) ? responseData : Array.isArray(responseData?.diffs) ? responseData.diffs : [];
      diffs.value = items as FileDiffItem[];
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
          if (event.type !== "turn.ended" || event.payload.sessionID !== activeSessionId) {
            return;
          }

          void fetchDiffs();
        },
      );

      onCleanup(unsubscribe);
    },
    { immediate: true },
  );

  return {
    diffs: readonly(diffs),
    available: readonly(available),
    isLoading: readonly(isLoading),
    isStale: readonly(isStale),
    error: readonly(error),
    fetchDiffs,
    markStale,
  };
}
