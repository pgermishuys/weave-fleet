import {
  onMounted,
  onUnmounted,
  readonly,
  shallowRef,
  toValue,
  watch,
  type MaybeRefOrGetter,
  type ShallowRef,
} from "vue";
import type { FleetSummaryResponse } from "@/api/client";
import { api } from "@/api/client";
import { extractApiError } from "@/lib/api-error";

export interface UseFleetSummaryOptions {
  pollIntervalMs?: MaybeRefOrGetter<number>;
  enabled?: MaybeRefOrGetter<boolean>;
}

export interface UseFleetSummaryResult {
  summary: Readonly<ShallowRef<FleetSummaryResponse | null>>;
  isLoading: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | undefined>>;
  refetch: () => Promise<void>;
}

const DEFAULT_POLL_INTERVAL_MS = 30_000;

export function useFleetSummary(options: UseFleetSummaryOptions = {}): UseFleetSummaryResult {
  const summary = shallowRef<FleetSummaryResponse | null>(null);
  const isLoading = shallowRef(false);
  const error = shallowRef<string | undefined>(undefined);
  const isVisible = shallowRef(true);

  let pollTimer: ReturnType<typeof setInterval> | undefined;
  let requestId = 0;
  let disposed = false;
  let hasLoadedOnce = false;

  function isEnabled(): boolean {
    return toValue(options.enabled) ?? true;
  }

  function pollIntervalMs(): number {
    return Math.max(0, toValue(options.pollIntervalMs) ?? DEFAULT_POLL_INTERVAL_MS);
  }

  function updateVisibility(): void {
    if (typeof document === "undefined") {
      isVisible.value = true;
      return;
    }

    isVisible.value = document.visibilityState === "visible";
  }

  async function fetchSummary(skipVisibilityGuard = false): Promise<void> {
    if (!isEnabled()) {
      isLoading.value = false;
      return;
    }

    if (!skipVisibilityGuard && !isVisible.value) {
      return;
    }

    const currentRequestId = ++requestId;
    if (!hasLoadedOnce) {
      isLoading.value = true;
    }

    try {
      const { data, error: apiError } = await api.GET("/api/fleet/summary");
      if (disposed || currentRequestId !== requestId) {
        return;
      }

      if (apiError || !data) {
        throw new Error(extractApiError(apiError, "No data returned"));
      }

      const typedData = data as FleetSummaryResponse;
      const previous = summary.value;
      summary.value = previous
        && previous.activeSessions === typedData.activeSessions
        && previous.idleSessions === typedData.idleSessions
        && previous.totalTokens === typedData.totalTokens
        && previous.totalCost === typedData.totalCost
        && previous.queuedTasks === typedData.queuedTasks
        ? previous
        : typedData;
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

    if (!isEnabled() || pollIntervalMs() <= 0) {
      return;
    }

    pollTimer = setInterval(() => {
      void fetchSummary();
    }, pollIntervalMs());
  }

  onMounted(() => {
    updateVisibility();

    if (typeof document !== "undefined") {
      document.addEventListener("visibilitychange", updateVisibility);
    }
  });

  onUnmounted(() => {
    disposed = true;
    stopPolling();

    if (typeof document !== "undefined") {
      document.removeEventListener("visibilitychange", updateVisibility);
    }
  });

  watch(
    () => [toValue(options.enabled), toValue(options.pollIntervalMs)] as const,
    async ([enabled]) => {
      if (!(enabled ?? true)) {
        stopPolling();
        isLoading.value = false;
        return;
      }

      startPolling();
      await fetchSummary(true);
    },
    { immediate: true },
  );

  watch(isVisible, (visible) => {
    if (visible) {
      void fetchSummary(true);
    }
  });

  return {
    summary: readonly(summary),
    isLoading: readonly(isLoading),
    error: readonly(error),
    refetch: async () => fetchSummary(true),
  };
}
