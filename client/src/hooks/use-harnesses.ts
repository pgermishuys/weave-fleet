"use client";

import { useState, useEffect, useCallback } from "react";
import { apiFetch } from "@/lib/api-client";
import type { HarnessInfo } from "@/lib/api-types";

export interface UseHarnessesResult {
  harnesses: HarnessInfo[];
  isLoading: boolean;
  error?: string;
  refresh: () => void;
}

export function useHarnesses(): UseHarnessesResult {
  const [harnesses, setHarnesses] = useState<HarnessInfo[]>([]);
  const [isLoading, setIsLoading] = useState(true);
  const [error, setError] = useState<string | undefined>();

  const fetchHarnesses = useCallback(async () => {
    setIsLoading(true);
    setError(undefined);
    try {
      const response = await apiFetch("/api/harnesses");
      if (!response.ok) {
        const data = await response.json().catch(() => ({}));
        throw new Error((data as { error?: string }).error ?? `HTTP ${response.status}`);
      }
      const data = (await response.json()) as HarnessInfo[];
      setHarnesses(data);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to fetch harnesses");
    } finally {
      setIsLoading(false);
    }
  }, []);

  useEffect(() => {
    void fetchHarnesses();
  }, [fetchHarnesses]);

  return { harnesses, isLoading, error, refresh: fetchHarnesses };
}
