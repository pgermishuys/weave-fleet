
import { useState, useEffect, useCallback, useRef } from "react";
import type { AnalyticsSummary } from "@/lib/api-types";
import { apiFetch } from "@/lib/api-client";

export interface UseAnalyticsSummaryParams {
  from?: string;    // ISO date string
  to?: string;      // ISO date string
  projectId?: string;
}

export interface UseAnalyticsSummaryResult {
  summary: AnalyticsSummary | null;
  isLoading: boolean;
  error?: string;
}

const DEFAULT_POLL_INTERVAL_MS = 30_000;

export function useAnalyticsSummary(
  params?: UseAnalyticsSummaryParams,
  pollIntervalMs: number = DEFAULT_POLL_INTERVAL_MS
): UseAnalyticsSummaryResult {
  const [summary, setSummary] = useState<AnalyticsSummary | null>(null);
  const [isLoading, setIsLoading] = useState(true);
  const [error, setError] = useState<string | undefined>();
  const isMounted = useRef(true);

  const fetchSummary = useCallback(async () => {
    if (typeof document !== "undefined" && document.visibilityState !== "visible") {
      return;
    }
    try {
      const query = new URLSearchParams();
      if (params?.from) query.set("from", params.from);
      if (params?.to) query.set("to", params.to);
      if (params?.projectId) query.set("projectId", params.projectId);
      const qs = query.toString();
      const response = await apiFetch(`/api/analytics/summary${qs ? `?${qs}` : ""}`);
      if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
      }
      const data = (await response.json()) as AnalyticsSummary;
      if (isMounted.current) {
        setSummary(data);
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
    fetchSummary();
    const interval = setInterval(fetchSummary, pollIntervalMs);

    const handleVisibilityChange = () => {
      if (document.visibilityState === "visible") {
        fetchSummary();
      }
    };
    document.addEventListener("visibilitychange", handleVisibilityChange);

    return () => {
      isMounted.current = false;
      clearInterval(interval);
      document.removeEventListener("visibilitychange", handleVisibilityChange);
    };
  }, [fetchSummary, pollIntervalMs]);

  return { summary, isLoading, error };
}
