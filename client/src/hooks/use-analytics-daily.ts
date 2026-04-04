"use client";

import { useState, useEffect, useCallback, useRef } from "react";
import type { DailyAnalytics } from "@/lib/api-types";
import { apiFetch } from "@/lib/api-client";

export interface UseAnalyticsDailyResult {
  daily: DailyAnalytics[];
  isLoading: boolean;
  error?: string;
}

const DEFAULT_POLL_INTERVAL_MS = 30_000;

export function useAnalyticsDaily(
  params?: { from?: string; to?: string; projectId?: string },
  pollIntervalMs: number = DEFAULT_POLL_INTERVAL_MS
): UseAnalyticsDailyResult {
  const [daily, setDaily] = useState<DailyAnalytics[]>([]);
  const [isLoading, setIsLoading] = useState(true);
  const [error, setError] = useState<string | undefined>();
  const isMounted = useRef(true);

  const fetchDaily = useCallback(async () => {
    if (typeof document !== "undefined" && document.visibilityState !== "visible") {
      return;
    }
    try {
      const query = new URLSearchParams();
      if (params?.from) query.set("from", params.from);
      if (params?.to) query.set("to", params.to);
      if (params?.projectId) query.set("projectId", params.projectId);
      const qs = query.toString();
      const response = await apiFetch(`/api/analytics/daily${qs ? `?${qs}` : ""}`);
      if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
      }
      const data = (await response.json()) as DailyAnalytics[];
      if (isMounted.current) {
        setDaily(data);
        setError(undefined);
      }
    } catch (err) {
      if (isMounted.current) {
        setError(err instanceof Error ? err.message : String(err));
      }
    } finally {
      if (isMounted.current) {
        setIsLoading(false);
      }
    }
  }, [params?.from, params?.to, params?.projectId]);

  useEffect(() => {
    isMounted.current = true;
    setIsLoading(true);
    fetchDaily();
    const interval = setInterval(fetchDaily, pollIntervalMs);

    const handleVisibilityChange = () => {
      if (document.visibilityState === "visible") {
        fetchDaily();
      }
    };
    document.addEventListener("visibilitychange", handleVisibilityChange);

    return () => {
      isMounted.current = false;
      clearInterval(interval);
      document.removeEventListener("visibilitychange", handleVisibilityChange);
    };
  }, [fetchDaily, pollIntervalMs]);

  return { daily, isLoading, error };
}
