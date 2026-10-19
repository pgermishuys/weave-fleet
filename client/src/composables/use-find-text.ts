import { computed, onUnmounted, shallowRef, toValue, watch, type MaybeRefOrGetter } from "vue";
import { useMachineTarget } from "@/lib/machine-target";
import type { TextSearchResult } from "@/lib/find-text";

/** Typing waits this long, so a word is searched once, not once per letter. */
export const FIND_TEXT_DEBOUNCE_MS = 250;
/** Shorter than this searches nothing: one letter matches almost every line. */
export const FIND_TEXT_MIN_CHARS = 2;

/** Why a search gave no answer. Never shown as "no matches". */
export interface FindTextFailure {
  /** "pattern": the regex is bad. "unsupported": that machine's Fleet is too old. "failed": anything else. */
  kind: "pattern" | "unsupported" | "failed";
  message: string;
}

export interface UseFindTextOptions {
  regex: MaybeRefOrGetter<boolean>;
  matchCase: MaybeRefOrGetter<boolean>;
}

/**
 * Searches inside the files of a session's folder as the query is typed (`GET /api/sessions/{id}/find/text`) on the
 * machine the session lives on. Until a new answer comes, the last one stays in view.
 */
export function useFindText(
  sessionId: MaybeRefOrGetter<string>,
  query: MaybeRefOrGetter<string>,
  options: UseFindTextOptions,
) {
  const { api } = useMachineTarget();
  const result = shallowRef<TextSearchResult | null>(null);
  const failure = shallowRef<FindTextFailure | null>(null);
  const isLoading = shallowRef(false);
  const tookMs = shallowRef(0);
  const trimmed = computed(() => toValue(query).trim());
  const isActive = computed(() => trimmed.value.length >= FIND_TEXT_MIN_CHARS);

  let timer: ReturnType<typeof setTimeout> | undefined;
  let controller: AbortController | undefined;

  function cancel(): void {
    if (timer) clearTimeout(timer);
    timer = undefined;
    controller?.abort();
    controller = undefined;
  }

  async function search(id: string, text: string, signal: AbortSignal): Promise<void> {
    const started = performance.now();
    const { data, error, response } = await api.GET("/api/sessions/{id}/find/text", {
      params: {
        path: { id },
        query: { q: text, regex: toValue(options.regex), matchCase: toValue(options.matchCase) },
      },
      signal,
    });
    if (signal.aborted) return;

    if (response.status === 404) {
      failure.value = { kind: "unsupported", message: "This machine can't search inside files yet." };
      result.value = null;
      return;
    }

    if (response.status === 400) {
      const payload = error as { error?: string } | undefined;
      failure.value = { kind: "pattern", message: payload?.error ?? "That isn't a valid pattern." };
      result.value = null;
      return;
    }

    if (error || !response.ok) {
      const payload = error as { error?: string } | undefined;
      failure.value = { kind: "failed", message: payload?.error ?? `The search failed (HTTP ${response.status}).` };
      result.value = null;
      return;
    }

    failure.value = null;
    result.value = data as unknown as TextSearchResult;
    tookMs.value = performance.now() - started;
  }

  watch(
    [() => toValue(sessionId), trimmed, () => toValue(options.regex), () => toValue(options.matchCase)],
    ([id, text], previous) => {
      cancel();
      if (previous && previous[0] !== id) result.value = null;
      if (text.length < FIND_TEXT_MIN_CHARS) {
        result.value = null;
        failure.value = null;
        isLoading.value = false;
        return;
      }

      isLoading.value = true;
      timer = setTimeout(() => {
        const request = new AbortController();
        controller = request;
        void search(id, text, request.signal)
          .catch((cause: unknown) => {
            if (request.signal.aborted) return;
            failure.value = { kind: "failed", message: cause instanceof Error ? cause.message : "The search failed." };
            result.value = null;
          })
          .finally(() => {
            if (request.signal.aborted) return;
            isLoading.value = false;
            controller = undefined;
          });
      }, FIND_TEXT_DEBOUNCE_MS);
    },
    { immediate: true },
  );

  onUnmounted(cancel);

  return { result, failure, isLoading, tookMs, isActive };
}
