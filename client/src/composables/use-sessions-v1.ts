import {
  computed,
  onMounted,
  onUnmounted,
  readonly,
  shallowRef,
  toValue,
  watch,
  type ComputedRef,
  type MaybeRefOrGetter,
  type Ref,
  type ShallowRef,
} from "vue";
import type { SessionListItem } from "@/lib/api-types";
import { apiFetch } from "@/lib/api-client";
import { sessionsChanged } from "@/lib/session-utils";
import { useSessionsV1Store } from "@/stores/sessions-v1";

export interface UseSessionsV1Options {
  retentionStatus?: MaybeRefOrGetter<"active" | "archived" | "all">;
  pollIntervalMs?: MaybeRefOrGetter<number>;
  enabled?: MaybeRefOrGetter<boolean>;
  initialPage?: number;
  initialPageSize?: number;
}

export interface UseSessionsV1Result {
  sessions: Readonly<Ref<readonly SessionListItem[]>>;
  isLoading: Readonly<ShallowRef<boolean>>;
  isRefreshing: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | undefined>>;
  page: Readonly<ShallowRef<number>>;
  pageSize: Readonly<ShallowRef<number>>;
  offset: ComputedRef<number>;
  retentionStatus: ComputedRef<"active" | "archived" | "all">;
  hasNextPage: ComputedRef<boolean>;
  hasPreviousPage: ComputedRef<boolean>;
  refetch: () => Promise<void>;
  setPage: (nextPage: number) => void;
  nextPage: () => void;
  previousPage: () => void;
  setPageSize: (nextPageSize: number) => void;
}

const DEFAULT_POLL_INTERVAL_MS = 15_000;
const DEFAULT_PAGE_SIZE = 100;

export function useSessionsV1(options: UseSessionsV1Options = {}): UseSessionsV1Result {
  const sessionsStore = useSessionsV1Store();
  const isLoading = shallowRef(false);
  const isRefreshing = shallowRef(false);
  const error = shallowRef<string | undefined>(undefined);
  const page = shallowRef(Math.max(1, options.initialPage ?? 1));
  const pageSize = shallowRef(Math.max(1, options.initialPageSize ?? DEFAULT_PAGE_SIZE));
  const isVisible = shallowRef(true);

  const retentionStatus = computed(() => toValue(options.retentionStatus) ?? "active");
  const pollIntervalMs = computed(() => Math.max(0, toValue(options.pollIntervalMs) ?? DEFAULT_POLL_INTERVAL_MS));
  const isEnabled = computed(() => toValue(options.enabled) ?? true);
  const offset = computed(() => (page.value - 1) * pageSize.value);
  const hasPreviousPage = computed(() => page.value > 1);
  const currentPageSessions = computed<SessionListItem[]>(() => sessionsStore.sessions);
  const hasNextPage = computed(() => currentPageSessions.value.length >= pageSize.value);

  let pollTimer: ReturnType<typeof setInterval> | undefined;
  let requestId = 0;
  let disposed = false;
  let hasLoadedOnce = false;

  function updateVisibility(): void {
    if (typeof document === "undefined") {
      isVisible.value = true;
      return;
    }

    isVisible.value = document.visibilityState === "visible";
  }

  async function fetchSessions(skipVisibilityGuard = false): Promise<void> {
    if (!isEnabled.value) {
      isLoading.value = false;
      isRefreshing.value = false;
      return;
    }

    if (!skipVisibilityGuard && !isVisible.value) {
      return;
    }

    const currentRequestId = ++requestId;
    const shouldShowInitialLoader = !hasLoadedOnce && currentPageSessions.value.length === 0;

    if (shouldShowInitialLoader) {
      isLoading.value = true;
    } else {
      isRefreshing.value = true;
    }

    try {
      const params = new URLSearchParams({
        limit: String(pageSize.value),
        offset: String(offset.value),
      });

      if (retentionStatus.value !== "active") {
        params.set("retentionStatus", retentionStatus.value);
      }

      const response = await apiFetch(`/api/sessions-v1?${params.toString()}`);
      if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
      }

      const data = (await response.json()) as SessionListItem[];
      if (disposed || currentRequestId !== requestId) {
        return;
      }

      if (sessionsChanged(sessionsStore.sessions, data)) {
        sessionsStore.setSessions(data);
      }

      error.value = undefined;
      hasLoadedOnce = true;
    } catch (fetchError) {
      if (disposed || currentRequestId !== requestId) {
        return;
      }

      error.value = fetchError instanceof Error ? fetchError.message : String(fetchError);
      hasLoadedOnce = true;
    } finally {
      if (disposed || currentRequestId !== requestId) {
        return;
      }

      isLoading.value = false;
      isRefreshing.value = false;
    }
  }

  function stopPolling(): void {
    if (!pollTimer) {
      return;
    }

    clearInterval(pollTimer);
    pollTimer = undefined;
  }

  function startPolling(): void {
    stopPolling();

    if (!isEnabled.value || pollIntervalMs.value <= 0) {
      return;
    }

    pollTimer = setInterval(() => {
      void fetchSessions();
    }, pollIntervalMs.value);
  }

  function setPage(nextPage: number): void {
    page.value = Math.max(1, Math.floor(nextPage));
  }

  function nextPage(): void {
    setPage(page.value + 1);
  }

  function previousPage(): void {
    setPage(page.value - 1);
  }

  function setPageSize(nextPageSize: number): void {
    pageSize.value = Math.max(1, Math.floor(nextPageSize));
    page.value = 1;
  }

  onMounted(() => {
    updateVisibility();

    if (typeof document !== "undefined") {
      document.addEventListener("visibilitychange", updateVisibility);
    }

    startPolling();
  });

  onUnmounted(() => {
    disposed = true;
    stopPolling();

    if (typeof document !== "undefined") {
      document.removeEventListener("visibilitychange", updateVisibility);
    }
  });

  watch(
    () => [retentionStatus.value, page.value, pageSize.value, isEnabled.value] as const,
    async ([, , , enabled]) => {
      if (!enabled) {
        isLoading.value = false;
        isRefreshing.value = false;
        return;
      }

      await fetchSessions();
    },
    { immediate: true },
  );

  watch(
    () => [pollIntervalMs.value, isEnabled.value] as const,
    () => {
      startPolling();
    },
    { immediate: true },
  );

  watch(isVisible, (visible) => {
    if (visible) {
      void fetchSessions();
    }
  });

  return {
    sessions: readonly(currentPageSessions),
    isLoading: readonly(isLoading),
    isRefreshing: readonly(isRefreshing),
    error: readonly(error),
    page: readonly(page),
    pageSize: readonly(pageSize),
    offset,
    retentionStatus,
    hasNextPage,
    hasPreviousPage,
    refetch: () => fetchSessions(true),
    setPage,
    nextPage,
    previousPage,
    setPageSize,
  };
}
