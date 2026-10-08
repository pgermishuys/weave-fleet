import { computed, onUnmounted, readonly, ref, shallowRef, toValue, watch, type MaybeRefOrGetter, type Ref, type ShallowRef } from "vue";
import { narrowFileMatches } from "@/lib/file-matches";
import { useMachineTarget } from "@/lib/machine-target";

interface FindFilesResponse {
  sessionId: string;
  files?: string[];
}

export interface UseFindFilesResult {
  files: Readonly<Ref<readonly string[]>>;
  isLoading: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | undefined>>;
}

/** Short: the list already follows each key from the last answer, so this only spares the server a burst of typing. */
const SEARCH_DEBOUNCE_MS = 80;

/**
 * Files and folders in the session's directory; folders end in "/". A null query asks for nothing.
 * An empty query, or one ending in "/", lists that folder straight away; anything else is a search,
 * debounced while typing. Until the answer comes, the last one is narrowed to what still matches the query.
 */
export function useFindFiles(sessionId: MaybeRefOrGetter<string | null | undefined>, query: MaybeRefOrGetter<string | null>): UseFindFilesResult {
  const { api } = useMachineTarget();
  // The server's last answer, and the query it answers.
  const answer = ref<{ query: string; files: string[] }>({ query: "", files: [] });
  const isLoading = shallowRef(false);
  const error = shallowRef<string | undefined>(undefined);
  const currentSessionId = computed(() => toValue(sessionId)?.trim() ?? "");
  const currentQuery = computed(() => toValue(query));
  const files = computed<readonly string[]>(() => {
    const typed = currentQuery.value?.trim();
    if (typed === undefined) {
      return [];
    }

    return typed === answer.value.query ? answer.value.files : narrowFileMatches(answer.value.files, typed);
  });

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
    answer.value = { query: trimmedQuery, files: Array.isArray(responseData.files) ? responseData.files : [] };
  }

  watch(
    [currentSessionId, currentQuery],
    ([activeSessionId, nextQuery], previous) => {
      cleanupPending();
      // Another session's files say nothing about this one's.
      if (previous && previous[0] !== activeSessionId) {
        answer.value = { query: "", files: [] };
      }

      if (!activeSessionId || nextQuery === null) {
        answer.value = { query: "", files: [] };
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
    files,
    isLoading: readonly(isLoading),
    error: readonly(error),
  };
}
