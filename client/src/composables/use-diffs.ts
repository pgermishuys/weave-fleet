import { readonly, ref, shallowRef, type Ref, type ShallowRef } from "vue";
import type { FileDiffItem } from "@/lib/api-types";
import { apiFetch } from "@/lib/api-client";

export interface UseDiffsResult {
  diffs: Readonly<Ref<readonly FileDiffItem[]>>;
  isLoading: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | undefined>>;
  fetchDiffs: () => Promise<void>;
}

export function useDiffs(sessionId: string, instanceId: string): UseDiffsResult {
  const diffs = ref<FileDiffItem[]>([]);
  const isLoading = shallowRef(false);
  const error = shallowRef<string | undefined>(undefined);

  let requestId = 0;

  async function fetchDiffs(): Promise<void> {
    if (!sessionId || !instanceId) {
      return;
    }

    const currentRequestId = ++requestId;
    isLoading.value = true;
    error.value = undefined;

    try {
      const url = `/api/sessions/${encodeURIComponent(sessionId)}/diffs?instanceId=${encodeURIComponent(instanceId)}`;
      const response = await apiFetch(url);
      if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
      }

      const data = await response.json();
      if (currentRequestId !== requestId) {
        return;
      }

      // API returns { diffs: [...] } wrapper object
      const items = Array.isArray(data) ? data : Array.isArray(data?.diffs) ? data.diffs : [];
      diffs.value = items as FileDiffItem[];
      error.value = undefined;
    } catch (fetchError) {
      if (currentRequestId !== requestId) {
        return;
      }

      error.value = fetchError instanceof Error ? fetchError.message : String(fetchError);
    } finally {
      if (currentRequestId === requestId) {
        isLoading.value = false;
      }
    }
  }

  return {
    diffs: readonly(diffs),
    isLoading: readonly(isLoading),
    error: readonly(error),
    fetchDiffs,
  };
}
