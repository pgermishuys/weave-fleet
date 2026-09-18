import { readonly, ref, shallowRef, watch, type Ref, type ShallowRef } from "vue";
import { api } from "@/api/client";
import type { HarnessInfo } from "@/api/client";

export interface UseHarnessesResult {
  harnesses: Readonly<Ref<readonly HarnessInfo[]>>;
  isLoading: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | undefined>>;
  refresh: () => Promise<void>;
}

/** Bumped when a harness may have changed (installed, signed in), so every list on screen checks again. */
const harnessesChanged = shallowRef(0);

/** Tells every `useHarnesses` on screen to fetch the list again, e.g. after installing a harness. */
export function refreshAllHarnesses(): void {
  harnessesChanged.value++;
}

export function useHarnesses(): UseHarnessesResult {
  const harnesses = ref<HarnessInfo[]>([]);
  const isLoading = shallowRef(true);
  const error = shallowRef<string | undefined>(undefined);

  async function fetchHarnesses(): Promise<void> {
    isLoading.value = true;
    error.value = undefined;

    try {
      const { data, error, response } = await api.GET("/api/harnesses");
      if (error || !response.ok) {
        const payload = error as { error?: string } | undefined;
        throw new Error(payload?.error ?? `HTTP ${response.status}`);
      }

      harnesses.value = data as unknown as HarnessInfo[];
    } catch (fetchError) {
      error.value = fetchError instanceof Error ? fetchError.message : "Failed to fetch harnesses";
    } finally {
      isLoading.value = false;
    }
  }

  void fetchHarnesses();
  watch(harnessesChanged, () => void fetchHarnesses());

  return {
    harnesses: readonly(harnesses),
    isLoading: readonly(isLoading),
    error: readonly(error),
    refresh: fetchHarnesses,
  };
}
