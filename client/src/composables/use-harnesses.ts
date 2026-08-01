import { readonly, ref, shallowRef, type Ref, type ShallowRef } from "vue";
import { api } from "@/api/client";
import type { HarnessInfo } from "@/api/client";

export interface UseHarnessesResult {
  harnesses: Readonly<Ref<readonly HarnessInfo[]>>;
  isLoading: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | undefined>>;
  refresh: () => Promise<void>;
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
        throw new Error((payload as any)?.error ?? `HTTP ${response.status}`);
      }

      harnesses.value = data as unknown as HarnessInfo[];
    } catch (fetchError) {
      error.value = fetchError instanceof Error ? fetchError.message : "Failed to fetch harnesses";
    } finally {
      isLoading.value = false;
    }
  }

  void fetchHarnesses();

  return {
    harnesses: readonly(harnesses),
    isLoading: readonly(isLoading),
    error: readonly(error),
    refresh: fetchHarnesses,
  };
}
