
import { useState, useEffect, useCallback, useRef } from "react";
import type { SessionAnalytics } from "@/lib/api-types";
import { apiFetch } from "@/lib/api-client";

export interface UseAnalyticsSessionsResult {
  sessions: SessionAnalytics[];
  isLoading: boolean;
  error?: string;
}

const DEFAULT_POLL_INTERVAL_MS = 30_000;

export function useAnalyticsSessions(
  params?: { from?: string; to?: string; projectId?: string; limit?: number },
  pollIntervalMs: number = DEFAULT_POLL_INTERVAL_MS
): UseAnalyticsSessionsResult {
  const [sessions, setSessions] = useState<SessionAnalytics[]>([]);
  const [isLoading, setIsLoading] = useState(true);
  const [error, setError] = useState<string | undefined>();
  const isMounted = useRef(true);

  const fetchSessions = useCallback(async () => {
    if (typeof document !== "undefined" && document.visibilityState !== "visible") {
      return;
    }
    try {
      const query = new URLSearchParams();
      if (params?.from) query.set("from", params.from);
      if (params?.to) query.set("to", params.to);
      if (params?.projectId) query.set("projectId", params.projectId);
      if (params?.limit != null) query.set("limit", String(params.limit));
      const qs = query.toString();
      const response = await apiFetch(`/api/analytics/sessions${qs ? `?${qs}` : ""}`);
      if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
      }
      const data = (await response.json()) as SessionAnalytics[];
      if (isMounted.current) {
        setSessions(data);
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
  }, [params?.from, params?.to, params?.projectId, params?.limit]);

  useEffect(() => {
    isMounted.current = true;
    setIsLoading(true);
    fetchSessions();
    const interval = setInterval(fetchSessions, pollIntervalMs);

    const handleVisibilityChange = () => {
      if (document.visibilityState === "visible") {
        fetchSessions();
      }
    };
    document.addEventListener("visibilitychange", handleVisibilityChange);

    return () => {
      isMounted.current = false;
      clearInterval(interval);
      document.removeEventListener("visibilitychange", handleVisibilityChange);
    };
  }, [fetchSessions, pollIntervalMs]);

  return { sessions, isLoading, error };
}
