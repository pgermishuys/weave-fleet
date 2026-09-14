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

const SEARCH_DEBOUNCE_MS = 300;

/**
 * Files and folders in the session's directory; folders end in "/". A null query asks for nothing.
 * An empty query, or one ending in "/", lists that folder straight away; anything else is a search,
 * debounced while typing.
 */
export function useFindFiles(sessionId: MaybeRefOrGetter<string | null | undefined>, query: MaybeRefOrGetter<string | null>): UseFindFilesResult {
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
      throw new Error(payload?.error ?? `HTTP ${response.status}`);
    }

    const responseData = data as unknown as FindFilesResponse;
    files.value = Array.isArray(responseData.files) ? responseData.files : [];
  }

  watch(
    [currentSessionId, currentQuery],
    ([activeSessionId, nextQuery]) => {
      cleanupPending();

      if (!activeSessionId || nextQuery === null) {
        files.value = [];
        isLoading.value = false;
        error.value = undefined;
        return;
      }

      const trimmedQuery = nextQuery.trim();
      const isListing = trimmedQuery === "" || trimmedQuery.endsWith("/");
      isLoading.value = true;

      timeoutId = setTimeout(() => {
        const request = new AbortController();
        controller = request;
        error.value = undefined;

        void fetchFiles(activeSessionId, trimmedQuery, request.signal)
          .catch((fetchError: unknown) => {
            if (request.signal.aborted) {
              return;
            }

            error.value = fetchError instanceof Error ? fetchError.message : "Failed to search files";
          })
          .finally(() => {
            // A newer request owns the loading state once this one is aborted.
            if (request.signal.aborted) {
              return;
            }

            isLoading.value = false;
            controller = undefined;
          });
      }, isListing ? 0 : SEARCH_DEBOUNCE_MS);
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
