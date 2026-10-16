import { computed, onUnmounted, readonly, ref, shallowRef, toValue, watch, type MaybeRefOrGetter, type Ref, type ShallowRef } from "vue";
import { narrowFileMatches } from "@/lib/file-matches";
import type { ApiClient } from "@/api/client";
import { useMachineTarget } from "@/lib/machine-target";

interface FindFilesResponse {
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
 * Files and folders in `directory` matching `query`, as the session composer's search finds them in a session's
 * directory (see `useFindFiles`).
 */
export async function findFilesInFolder(api: ApiClient, directory: string, query: string, signal?: AbortSignal): Promise<string[]> {
  const { data, error, response } = await api.GET("/api/directories/find", {
    params: { query: { path: directory, q: query } },
    signal,
  });
  if (error || !response.ok) {
    const payload = error as { error?: string } | undefined;
    throw new Error(payload?.error ?? `HTTP ${response.status}`);
  }

  const files = (data as unknown as FindFilesResponse).files;
  return Array.isArray(files) ? files : [];
}

/**
 * Files and folders in the session's directory, or, with no session yet, in `directory`; folders end in "/". A null
 * query asks for nothing. An empty query, or one ending in "/", lists that folder straight away; anything else is a
 * search, debounced while typing. Until the answer comes, the last one is narrowed to what still matches the query.
 */
export function useFindFiles(
  sessionId: MaybeRefOrGetter<string | null | undefined>,
  query: MaybeRefOrGetter<string | null>,
  directory: MaybeRefOrGetter<string | null | undefined> = null,
): UseFindFilesResult {
  const { api } = useMachineTarget();
  // The server's last answer, and the query it answers.
  const answer = ref<{ query: string; files: string[] }>({ query: "", files: [] });
  const isLoading = shallowRef(false);
  const error = shallowRef<string | undefined>(undefined);
  const currentSessionId = computed(() => toValue(sessionId)?.trim() ?? "");
  const currentDirectory = computed(() => (currentSessionId.value ? "" : toValue(directory)?.trim() ?? ""));
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

  async function fetchFiles(activeSessionId: string, activeDirectory: string, trimmedQuery: string, signal: AbortSignal): Promise<void> {
    if (!activeSessionId) {
      answer.value = { query: trimmedQuery, files: await findFilesInFolder(api, activeDirectory, trimmedQuery, signal) };
      return;
    }

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
    [currentSessionId, currentDirectory, currentQuery],
    ([activeSessionId, activeDirectory, nextQuery], previous) => {
      cleanupPending();
      // Another session's (or folder's) files say nothing about this one's.
      if (previous && (previous[0] !== activeSessionId || previous[1] !== activeDirectory)) {
        answer.value = { query: "", files: [] };
      }

      if ((!activeSessionId && !activeDirectory) || nextQuery === null) {
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

        void fetchFiles(activeSessionId, activeDirectory, trimmedQuery, request.signal)
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
