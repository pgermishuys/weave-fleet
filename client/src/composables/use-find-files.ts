import { computed, onUnmounted, readonly, ref, shallowRef, toValue, watch, type MaybeRefOrGetter, type Ref, type ShallowRef } from "vue";
import { apiFetch } from "@/lib/api-client";

interface FindFilesResponse {
  sessionId: string;
  files?: string[];
}

export interface UseFindFilesResult {
  files: Readonly<Ref<readonly string[]>>;
  isLoading: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | undefined>>;
}

export function useFindFiles(sessionId: MaybeRefOrGetter<string | null | undefined>, query: MaybeRefOrGetter<string>): UseFindFilesResult {
  const files = ref<string[]>([]);
  const isLoading = shallowRef(false);
  const error = shallowRef<string | undefined>(undefined);
  const currentSessionId = computed(() => toValue(sessionId)?.trim() ?? "");
  const currentQuery = computed(() => toValue(query));

  let timeoutId: ReturnType<typeof setTimeout> | undefined;
  let controller: AbortController | undefined;

  function cleanupPending(): void {
    if (timeoutId) {
      clearTimeout(timeoutId);
      timeoutId = undefined;
    }

    controller?.abort();
    controller = undefined;
  }

  async function fetchFiles(activeSessionId: string, trimmedQuery: string, signal: AbortSignal): Promise<void> {
    const url = `/api/sessions/${encodeURIComponent(activeSessionId)}/find/files?q=${encodeURIComponent(trimmedQuery)}`;
    const response = await apiFetch(url, { signal });
    if (!response.ok) {
      const data = (await response.json().catch(() => ({}))) as { error?: string };
      throw new Error(data.error ?? `HTTP ${response.status}`);
    }

    const data = (await response.json()) as FindFilesResponse;
    files.value = Array.isArray(data.files) ? data.files : [];
  }

  watch(
    [currentSessionId, currentQuery],
    ([activeSessionId, nextQuery]) => {
      const trimmedQuery = nextQuery.trim();
      cleanupPending();

      if (!activeSessionId || trimmedQuery === "") {
        files.value = [];
        isLoading.value = false;
        error.value = undefined;
        return;
      }

      timeoutId = setTimeout(() => {
        controller = new AbortController();
        isLoading.value = true;
        error.value = undefined;

        void fetchFiles(activeSessionId, trimmedQuery, controller.signal)
          .catch((fetchError: unknown) => {
            if (fetchError instanceof DOMException && fetchError.name === "AbortError") {
              return;
            }

            error.value = fetchError instanceof Error ? fetchError.message : "Failed to search files";
          })
          .finally(() => {
            isLoading.value = false;
            controller = undefined;
          });
      }, 300);
    },
    { immediate: true },
  );

  onUnmounted(() => {
    cleanupPending();
  });

  return {
    files: readonly(files),
    isLoading: readonly(isLoading),
    error: readonly(error),
  };
}
