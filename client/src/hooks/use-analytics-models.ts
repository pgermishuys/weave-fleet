
import { useState, useEffect, useCallback, useRef } from "react";
import type { ModelAnalytics } from "@/lib/api-types";
import { apiFetch } from "@/lib/api-client";

export interface UseAnalyticsModelsResult {
  models: ModelAnalytics[];
  isLoading: boolean;
  error?: string;
}

const DEFAULT_POLL_INTERVAL_MS = 30_000;

export function useAnalyticsModels(
  params?: { from?: string; to?: string },
  pollIntervalMs: number = DEFAULT_POLL_INTERVAL_MS
): UseAnalyticsModelsResult {
  const [models, setModels] = useState<ModelAnalytics[]>([]);
  const [isLoading, setIsLoading] = useState(true);
  const [error, setError] = useState<string | undefined>();
  const isMounted = useRef(true);

  const fetchModels = useCallback(async () => {
    if (typeof document !== "undefined" && document.visibilityState !== "visible") {
      return;
    }
    try {
      const query = new URLSearchParams();
      if (params?.from) query.set("from", params.from);
      if (params?.to) query.set("to", params.to);
      const qs = query.toString();
      const response = await apiFetch(`/api/analytics/models${qs ? `?${qs}` : ""}`);
      if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
      }
      const data = (await response.json()) as ModelAnalytics[];
      if (isMounted.current) {
        setModels(data);
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
  }, [params?.from, params?.to]);

  useEffect(() => {
    isMounted.current = true;
    setIsLoading(true);
    fetchModels();
    const interval = setInterval(fetchModels, pollIntervalMs);

    const handleVisibilityChange = () => {
      if (document.visibilityState === "visible") {
        fetchModels();
      }
    };
    document.addEventListener("visibilitychange", handleVisibilityChange);

    return () => {
      isMounted.current = false;
      clearInterval(interval);
      document.removeEventListener("visibilitychange", handleVisibilityChange);
    };
  }, [fetchModels, pollIntervalMs]);

  return { models, isLoading, error };
}
