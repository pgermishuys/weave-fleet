import { readonly, ref, shallowRef, type Ref, type ShallowRef } from "vue";
import { apiFetch } from "@/lib/api-client";
import type { HarnessInfo } from "@/lib/api-types";

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
      const response = await apiFetch("/api/harnesses");
      if (!response.ok) {
        const data = (await response.json().catch(() => ({}))) as { error?: string };
        throw new Error(data.error ?? `HTTP ${response.status}`);
      }

      harnesses.value = (await response.json()) as HarnessInfo[];
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
