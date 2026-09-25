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

function fetchHarnessList() {
  return api.GET("/api/harnesses");
}

/**
 * The request every list on screen shares while it runs. A page mounts many lists at once (one per session row), and
 * the server answers them one at a time, so separate requests left the last list waiting seconds for its answer.
 */
let sharedRequest: { generation: number; answer: ReturnType<typeof fetchHarnessList> } | undefined;

/** The list's answer: the request already running for this change, or a new one (always, when `fresh`). */
function requestHarnessList(fresh: boolean): ReturnType<typeof fetchHarnessList> {
  if (fresh || sharedRequest?.generation !== harnessesChanged.value) {
    const entry = { generation: harnessesChanged.value, answer: fetchHarnessList() };
    sharedRequest = entry;
    const forget = () => {
      if (sharedRequest === entry) sharedRequest = undefined;
    };
    entry.answer.then(forget, forget);
  }
  return sharedRequest!.answer;
}

export function useHarnesses(): UseHarnessesResult {
  const harnesses = ref<HarnessInfo[]>([]);
  const isLoading = shallowRef(true);
  const error = shallowRef<string | undefined>(undefined);

  /** Numbers each fetch, so a slow older answer can't overwrite a newer one (checks overlap while an update runs). */
  let latestRequest = 0;

  async function fetchHarnesses(fresh = false): Promise<void> {
    const request = ++latestRequest;
    isLoading.value = true;
    error.value = undefined;

    try {
      const { data, error, response } = await requestHarnessList(fresh);
      if (request !== latestRequest) return;
      if (error || !response.ok) {
        const payload = error as { error?: string } | undefined;
        throw new Error(payload?.error ?? `HTTP ${response.status}`);
      }

      harnesses.value = data as unknown as HarnessInfo[];
    } catch (fetchError) {
      if (request === latestRequest) {
        error.value = fetchError instanceof Error ? fetchError.message : "Failed to fetch harnesses";
      }
    } finally {
      if (request === latestRequest) isLoading.value = false;
    }
  }

  void fetchHarnesses();
  watch(harnessesChanged, () => void fetchHarnesses());

  return {
    harnesses: readonly(harnesses),
    isLoading: readonly(isLoading),
    error: readonly(error),
    refresh: () => fetchHarnesses(true),
  };
}
