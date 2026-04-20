import { computed, onMounted, onUnmounted, shallowRef, toValue, type ComputedRef, type MaybeRefOrGetter, type ShallowRef } from "vue";
import type { ModelAnalytics } from "@/lib/api-types";
import { apiFetch } from "@/lib/api-client";

export interface UseAnalyticsModelsParams {
  from?: MaybeRefOrGetter<string | undefined>;
  projectId?: MaybeRefOrGetter<string | undefined>;
  to?: MaybeRefOrGetter<string | undefined>;
}

export interface UseAnalyticsModelsResult {
  models: ShallowRef<ModelAnalytics[]>;
  isLoading: ShallowRef<boolean>;
  error: ShallowRef<string | undefined>;
  refetch: () => Promise<void>;
  queryString: ComputedRef<string>;
}

const DEFAULT_POLL_INTERVAL_MS = 30_000;

export function useAnalyticsModels(
  params: UseAnalyticsModelsParams = {},
  pollIntervalMs: MaybeRefOrGetter<number> = DEFAULT_POLL_INTERVAL_MS,
): UseAnalyticsModelsResult {
  const models = shallowRef<ModelAnalytics[]>([]);
  const isLoading = shallowRef(true);
  const error = shallowRef<string | undefined>(undefined);

  let timer: ReturnType<typeof setInterval> | undefined;
  let requestId = 0;

  const queryString = computed(() => {
    const query = new URLSearchParams();
    const from = toValue(params.from);
    const projectId = toValue(params.projectId);
    const to = toValue(params.to);

    if (from) {
      query.set("from", from);
    }
    if (projectId) {
      query.set("projectId", projectId);
    }
    if (to) {
      query.set("to", to);
    }

    return query.toString();
  });

  async function fetchModels(): Promise<void> {
    if (typeof document !== "undefined" && document.visibilityState !== "visible") {
      return;
    }

    const currentRequestId = ++requestId;

    try {
      const response = await apiFetch(`/api/analytics/models${queryString.value ? `?${queryString.value}` : ""}`);
      if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
      }

      const data = (await response.json()) as ModelAnalytics[];
      if (currentRequestId !== requestId) {
        return;
      }

      models.value = data;
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
      void fetchModels();
    }, Math.max(0, toValue(pollIntervalMs)));
  }

  function handleVisibilityChange(): void {
    if (document.visibilityState === "visible") {
      void fetchModels();
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
    models,
    isLoading,
    error,
    refetch: fetchModels,
    queryString,
  };
}
