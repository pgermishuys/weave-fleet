import { computed, readonly, shallowRef, toValue, watch, type ComputedRef, type MaybeRefOrGetter, type Ref, type ShallowRef } from "vue";
import type { FileDiffItem, SessionDiffBase, SessionDiffsResponse } from "@/api/client";
import { useWeaveSocket } from "@/composables/use-weave-socket";
import type { DomainEvent } from "@/lib/domain-events";
import { useMachineTarget } from "@/lib/machine-target";

export type GitRefusal = NonNullable<SessionDiffsResponse["refusal"]>;

export interface UseDiffsResult {
  /** The changed files with their line counts. Contents aren't included; see {@link fetchFileDiff}. */
  diffs: Readonly<Ref<readonly FileDiffItem[]>>;
  /** The same list by path, for lookups from every row of a file tree. */
  byFile: ComputedRef<ReadonlyMap<string, FileDiffItem>>;
  available: Readonly<ShallowRef<boolean>>;
  /** Why the changes are unavailable when git refuses the folder; null otherwise. */
  refusal: Readonly<ShallowRef<GitRefusal | null>>;
  /** What the changes are compared with; it moves when the branch is rebased onto a newer main. */
  base: Readonly<ShallowRef<SessionDiffBase | null>>;
  isLoading: Readonly<ShallowRef<boolean>>;
  /** True once the first /diffs answer (or failure) has arrived for this session; until then an empty list means "not known yet". */
  hasLoaded: Readonly<ShallowRef<boolean>>;
  isStale: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | undefined>>;
  /**
   * Asks /diffs again. `join`: share a request for this session that is already on its way (opening a session, when the
   * page and the right panel both ask); leave it off after an edit or a revert, so the answer can't predate the change.
   */
  fetchDiffs: (options?: { join?: boolean }) => Promise<void>;
  markStale: () => void;
}

type DiffsAnswer = { data?: unknown; error?: unknown; response: Response };
/** /diffs requests on their way, by machine and session, for callers that join one. */
const pendingLists = new Map<string, Promise<DiffsAnswer>>();

function listKey(items: readonly FileDiffItem[]): string {
  return items.map((item) => `${item.file}\u0000${item.status}\u0000${item.additions}\u0000${item.deletions}`).join("\n");
}

/** Identifies a diff base: a file's base contents are the same for as long as this is. */
export function baseKey(base: SessionDiffBase | null | undefined): string {
  if (!base) return "";
  return base.kind === "branch" ? `branch:${base.commit ?? ""}` : "session";
}

export interface UseDiffsOptions {
  /** False while nothing shows the diffs (the right panel is closed): file changes only mark them stale, and they load once this turns true. */
  isWanted?: () => boolean;
}

export function useDiffs(
  sessionId: MaybeRefOrGetter<string | null | undefined>,
  options: UseDiffsOptions = {},
): UseDiffsResult {
  const machine = useMachineTarget();
  // Replaced whole, never mutated: a session can change hundreds of files.
  const diffs = shallowRef<readonly FileDiffItem[]>([]);
  const available = shallowRef(false);
  const refusal = shallowRef<GitRefusal | null>(null);
  const base = shallowRef<SessionDiffBase | null>(null);
  const isLoading = shallowRef(false);
  const hasLoaded = shallowRef(false);
  const isStale = shallowRef(false);
  const error = shallowRef<string | undefined>(undefined);
  const currentSessionId = computed(() => toValue(sessionId) ?? "");
  const { subscribeV2 } = useWeaveSocket(machine);

  let requestId = 0;
  let debounceTimeoutId: ReturnType<typeof setTimeout> | undefined;

  async function fetchDiffs(options: { join?: boolean } = {}): Promise<void> {
    const activeSessionId = currentSessionId.value;

    if (!activeSessionId) {
      requestId += 1;
      diffs.value = [];
      available.value = false;
      base.value = null;
      isLoading.value = false;
      hasLoaded.value = false;
      isStale.value = false;
      error.value = undefined;
      return;
    }

    const currentRequestId = ++requestId;
    isLoading.value = true;
    error.value = undefined;

    try {
      const key = `${machine.key}\u0000${activeSessionId}`;
      let pending = options.join ? pendingLists.get(key) : undefined;
      if (!pending) {
        const request: Promise<DiffsAnswer> = machine.api.GET("/api/sessions/{id}/diffs", {
          params: {
            path: { id: activeSessionId },
          },
        });
        pending = request;
        pendingLists.set(key, request);
        const forget = () => {
          if (pendingLists.get(key) === request) pendingLists.delete(key);
        };
        request.then(forget, forget);
      }
      const { data, error: apiError, response } = await pending;

      if (apiError || !response.ok) {
        throw new Error(`HTTP ${response.status}`);
      }

      if (currentRequestId !== requestId) {
        return;
      }

      // Response body is not typed in schema, use data from openapi-fetch
      const responseData = data as unknown as SessionDiffsResponse | FileDiffItem[] | undefined;

      // API returns { diffs: [...], available: boolean } wrapper object.
      const items: readonly FileDiffItem[] = Array.isArray(responseData) ? responseData : Array.isArray(responseData?.diffs) ? responseData.diffs : [];
      // Keep the old list when nothing changed, so what's derived from it doesn't recompute on every edit event.
      if (listKey(items) !== listKey(diffs.value)) diffs.value = items;
      available.value = Array.isArray(responseData) || typeof responseData?.available !== "boolean" ? true : responseData.available;
      refusal.value = Array.isArray(responseData) ? null : (responseData?.refusal ?? null);
      const nextBase = Array.isArray(responseData) ? null : (responseData?.base ?? null);
      if (baseKey(nextBase) !== baseKey(base.value)) base.value = nextBase;
      isStale.value = false;
      error.value = undefined;
    } catch (fetchError) {
      if (currentRequestId !== requestId) {
        return;
      }

      available.value = false;
      base.value = null;
      error.value = fetchError instanceof Error ? fetchError.message : String(fetchError);
    } finally {
      if (currentRequestId === requestId) {
        isLoading.value = false;
        hasLoaded.value = true;
      }
    }
  }

  function markStale(): void {
    if (!currentSessionId.value) {
      return;
    }

    isStale.value = true;
  }

  function debouncedFetchDiffs(): void {
    if (options.isWanted && !options.isWanted()) {
      markStale();
      return;
    }

    if (debounceTimeoutId) {
      clearTimeout(debounceTimeoutId);
    }

    debounceTimeoutId = setTimeout(() => {
      void fetchDiffs();
      debounceTimeoutId = undefined;
    }, 500);
  }

  watch(
    currentSessionId,
    () => {
      requestId += 1;
      diffs.value = [];
      available.value = false;
      base.value = null;
      isLoading.value = false;
      hasLoaded.value = false;
      isStale.value = false;
      error.value = undefined;
    },
    { immediate: true },
  );

  if (options.isWanted) {
    const isWanted = options.isWanted;
    watch(isWanted, (wanted) => {
      if (wanted && isStale.value && currentSessionId.value && !isLoading.value) {
        void fetchDiffs();
      }
    });
  }

  watch(
    currentSessionId,
    (activeSessionId, _previousSessionId, onCleanup) => {
      if (!activeSessionId) {
        return;
      }

      const unsubscribe = subscribeV2(
        `session:${activeSessionId}`,
        () => {
          // Diff state is loaded from the REST endpoint; snapshots are ignored here.
        },
        (event: DomainEvent) => {
          if (event.type === "turn.ended" && event.payload.sessionID === activeSessionId) {
            void fetchDiffs();
          } else if (event.type === "files.changed" && event.payload.sessionId === activeSessionId) {
            debouncedFetchDiffs();
          }
        },
      );

      onCleanup(() => {
        if (debounceTimeoutId) {
          clearTimeout(debounceTimeoutId);
          debounceTimeoutId = undefined;
        }
        unsubscribe();
      });
    },
    { immediate: true },
  );

  const byFile = computed(() => new Map(diffs.value.map((diff) => [diff.file, diff])));

  return {
    diffs: computed(() => diffs.value),
    byFile,
    available: readonly(available),
    refusal: readonly(refusal),
    base: readonly(base),
    isLoading: readonly(isLoading),
    hasLoaded: readonly(hasLoaded),
    isStale: readonly(isStale),
    error: readonly(error),
    fetchDiffs,
    markStale,
  };
}
