import { computed, onMounted, onUnmounted, shallowRef, toValue, type ComputedRef, type MaybeRefOrGetter, type ShallowRef } from "vue";
import type { DailyAnalytics } from "@/lib/api-types";
import { apiFetch } from "@/lib/api-client";

export interface UseAnalyticsDailyParams {
  from?: MaybeRefOrGetter<string | undefined>;
  to?: MaybeRefOrGetter<string | undefined>;
  projectId?: MaybeRefOrGetter<string | undefined>;
}

export interface UseAnalyticsDailyResult {
  daily: ShallowRef<DailyAnalytics[]>;
  isLoading: ShallowRef<boolean>;
  error: ShallowRef<string | undefined>;
  refetch: () => Promise<void>;
  queryString: ComputedRef<string>;
}

const DEFAULT_POLL_INTERVAL_MS = 30_000;

export function useAnalyticsDaily(
  params: UseAnalyticsDailyParams = {},
  pollIntervalMs: MaybeRefOrGetter<number> = DEFAULT_POLL_INTERVAL_MS,
): UseAnalyticsDailyResult {
  const daily = shallowRef<DailyAnalytics[]>([]);
  const isLoading = shallowRef(true);
  const error = shallowRef<string | undefined>(undefined);

  let timer: ReturnType<typeof setInterval> | undefined;
  let requestId = 0;

  const queryString = computed(() => {
    const query = new URLSearchParams();

    const from = toValue(params.from);
    const to = toValue(params.to);
    const projectId = toValue(params.projectId);

    if (from) {
      query.set("from", from);
    }
    if (to) {
      query.set("to", to);
    }
    if (projectId) {
      query.set("projectId", projectId);
    }

    return query.toString();
  });

  async function fetchDaily(): Promise<void> {
    if (typeof document !== "undefined" && document.visibilityState !== "visible") {
      return;
    }

    const currentRequestId = ++requestId;

    try {
      const response = await apiFetch(`/api/analytics/daily${queryString.value ? `?${queryString.value}` : ""}`);
      if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
      }

      const data = (await response.json()) as DailyAnalytics[];
      if (currentRequestId !== requestId) {
        return;
      }

      daily.value = data;
      error.value = undefined;
    } catch (fetchError) {
      if (currentRequestId !== requestId) {
        return;
      }

      error.value = fetchError instanceof Error ? fetchError.message : String(fetchError);
    } finally {
      if (currentRequestId === requestId) {
        isLoading.value = false;
      }
    }
  }

  function startPolling(): void {
    if (timer) {
      clearInterval(timer);
    }

    timer = setInterval(() => {
      void fetchDaily();
    }, Math.max(0, toValue(pollIntervalMs)));
  }

  function handleVisibilityChange(): void {
    if (document.visibilityState === "visible") {
      void fetchDaily();
    }
  }

  onMounted(() => {
    startPolling();
    document.addEventListener("visibilitychange", handleVisibilityChange);
  });

  onUnmounted(() => {
    if (timer) {
      clearInterval(timer);
    }
    if (typeof document !== "undefined") {
      document.removeEventListener("visibilitychange", handleVisibilityChange);
    }
  });

  return {
    daily,
    isLoading,
    error,
    refetch: fetchDaily,
    queryString,
  };
}
