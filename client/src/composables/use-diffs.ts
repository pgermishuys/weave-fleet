import { computed, readonly, ref, shallowRef, toValue, watch, type MaybeRefOrGetter, type Ref, type ShallowRef } from "vue";
import type { FileDiffItem, SessionDiffsResponse } from "@/lib/api-types";
import { apiFetch } from "@/lib/api-client";
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
  instanceId: MaybeRefOrGetter<string | null | undefined>,
): UseDiffsResult {
  const diffs = ref<FileDiffItem[]>([]);
  const available = shallowRef(false);
  const isLoading = shallowRef(false);
  const isStale = shallowRef(false);
  const error = shallowRef<string | undefined>(undefined);
  const currentSessionId = computed(() => toValue(sessionId) ?? "");
  const currentInstanceId = computed(() => toValue(instanceId) ?? "");
  const { subscribeV2 } = useWeaveSocket();

  let requestId = 0;

  async function fetchDiffs(): Promise<void> {
    const activeSessionId = currentSessionId.value;
    const activeInstanceId = currentInstanceId.value;

    if (!activeSessionId || !activeInstanceId) {
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
      const url = `/api/sessions/${encodeURIComponent(activeSessionId)}/diffs?instanceId=${encodeURIComponent(activeInstanceId)}`;
      const response = await apiFetch(url);
      if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
      }

      const data = await response.json() as SessionDiffsResponse | FileDiffItem[] | undefined;
      if (currentRequestId !== requestId) {
        return;
      }

      // API returns { diffs: [...], available: boolean } wrapper object.
      const items = Array.isArray(data) ? data : Array.isArray(data?.diffs) ? data.diffs : [];
      diffs.value = items as FileDiffItem[];
      available.value = Array.isArray(data) || typeof data?.available !== "boolean" ? true : data.available;
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
    if (!currentSessionId.value || !currentInstanceId.value) {
      return;
    }

    isStale.value = true;
  }

  watch(
    [currentSessionId, currentInstanceId],
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
