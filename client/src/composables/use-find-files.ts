import { computed, onUnmounted, readonly, ref, shallowRef, toValue, watch, type MaybeRefOrGetter, type Ref, type ShallowRef } from "vue";
import { api } from "@/api/client";

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
    const { data, error, response } = await api.GET("/api/sessions/{id}/find/files", {
      params: {
        path: { id: activeSessionId },
        query: { q: trimmedQuery },
      },
      signal,
    });
    if (error || !response.ok) {
      const payload = error as { error?: string } | undefined;
      throw new Error((payload as any)?.error ?? `HTTP ${response.status}`);
    }

    const responseData = data as unknown as FindFilesResponse;
    files.value = Array.isArray(responseData.files) ? responseData.files : [];
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
