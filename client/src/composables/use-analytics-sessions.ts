import { computed, onMounted, onUnmounted, shallowRef, toValue, type ComputedRef, type MaybeRefOrGetter, type ShallowRef } from "vue";
import type { SessionAnalytics } from "@/lib/api-types";
import { apiFetch } from "@/lib/api-client";

export type AnalyticsSessionsSortBy = string;
export type AnalyticsSessionsSortDir = "asc" | "desc";

export interface UseAnalyticsSessionsParams {
  from?: MaybeRefOrGetter<string | undefined>;
  to?: MaybeRefOrGetter<string | undefined>;
  projectId?: MaybeRefOrGetter<string | undefined>;
  limit?: MaybeRefOrGetter<number | undefined>;
  sortBy?: MaybeRefOrGetter<AnalyticsSessionsSortBy | undefined>;
  sortDir?: MaybeRefOrGetter<AnalyticsSessionsSortDir | undefined>;
}

export interface UseAnalyticsSessionsResult {
  sessions: ShallowRef<SessionAnalytics[]>;
  isLoading: ShallowRef<boolean>;
  error: ShallowRef<string | undefined>;
  refetch: () => Promise<void>;
  queryString: ComputedRef<string>;
}

const DEFAULT_POLL_INTERVAL_MS = 30_000;

export function useAnalyticsSessions(
  params: UseAnalyticsSessionsParams = {},
  pollIntervalMs: MaybeRefOrGetter<number> = DEFAULT_POLL_INTERVAL_MS,
): UseAnalyticsSessionsResult {
  const sessions = shallowRef<SessionAnalytics[]>([]);
  const isLoading = shallowRef(true);
  const error = shallowRef<string | undefined>(undefined);

  let timer: ReturnType<typeof setInterval> | undefined;
  let requestId = 0;

  const queryString = computed(() => {
    const query = new URLSearchParams();
    const from = toValue(params.from);
    const to = toValue(params.to);
    const projectId = toValue(params.projectId);
    const limit = toValue(params.limit);
    const sortBy = toValue(params.sortBy);
    const sortDir = toValue(params.sortDir);

    if (from) {
      query.set("from", from);
    }
    if (to) {
      query.set("to", to);
    }
    if (projectId) {
      query.set("projectId", projectId);
    }
    if (limit != null) {
      query.set("limit", String(limit));
    }
    if (sortBy) {
      query.set("sortBy", sortBy);
    }
    if (sortDir) {
      query.set("sortDir", sortDir);
    }

    return query.toString();
  });

  async function fetchSessions(): Promise<void> {
    if (typeof document !== "undefined" && document.visibilityState !== "visible") {
      return;
    }

    const currentRequestId = ++requestId;

    try {
      const response = await apiFetch(`/api/analytics/sessions${queryString.value ? `?${queryString.value}` : ""}`);
      if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
      }

      const data = (await response.json()) as SessionAnalytics[];
      if (currentRequestId !== requestId) {
        return;
      }

      sessions.value = data;
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
      void fetchSessions();
    }, Math.max(0, toValue(pollIntervalMs)));
  }

  function handleVisibilityChange(): void {
    if (document.visibilityState === "visible") {
      void fetchSessions();
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
    sessions,
    isLoading,
    error,
    refetch: fetchSessions,
    queryString,
  };
}
