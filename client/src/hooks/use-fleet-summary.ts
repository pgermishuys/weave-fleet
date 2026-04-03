"use client";

import { useState, useEffect, useCallback, useRef } from "react";
import type { FleetSummaryResponse } from "@/lib/api-types";
import { apiFetch } from "@/lib/api-client";

export type { FleetSummaryResponse };

export interface UseFleetSummaryResult {
  summary: FleetSummaryResponse | null;
  isLoading: boolean;
  error?: string;
}

const DEFAULT_POLL_INTERVAL_MS = 30_000;

export function useFleetSummary(
  pollIntervalMs: number = DEFAULT_POLL_INTERVAL_MS
): UseFleetSummaryResult {
  const [summary, setSummary] = useState<FleetSummaryResponse | null>(null);
  const [isLoading, setIsLoading] = useState(true);
  const [error, setError] = useState<string | undefined>();
  const isMounted = useRef(true);

  const fetchSummary = useCallback(async () => {
    // Skip when tab is hidden — resume when it becomes visible again
    if (typeof document !== "undefined" && document.visibilityState !== "visible") {
      return;
    }
    try {
      const response = await apiFetch("/api/fleet/summary");
      if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
      }
      const data = (await response.json()) as FleetSummaryResponse;
      if (isMounted.current) {
        setSummary(prev => {
          if (
            prev &&
            prev.activeSessions === data.activeSessions &&
            prev.idleSessions === data.idleSessions &&
            prev.totalTokens === data.totalTokens &&
            prev.totalCost === data.totalCost &&
            prev.queuedTasks === data.queuedTasks
          ) return prev;
          return data;
        });
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
  }, []);

  useEffect(() => {
    isMounted.current = true;
    fetchSummary();
    const interval = setInterval(fetchSummary, pollIntervalMs);

    // Pause/resume polling on visibility change
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
