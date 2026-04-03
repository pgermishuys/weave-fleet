"use client";

import { useState, useEffect, useCallback, useRef } from "react";
import { apiFetch } from "@/lib/api-client";
import type { IntegrationStatusInfo } from "@/lib/api-types";

export type { IntegrationStatusInfo };

export interface UseIntegrationsResult {
  integrations: IntegrationStatusInfo[];
  isLoading: boolean;
  error?: string;
  connect: (id: string, config: Record<string, unknown>) => Promise<void>;
  disconnect: (id: string) => Promise<void>;
  refetch: () => void;
}

const POLL_INTERVAL_MS = 30_000;

export function useIntegrations(): UseIntegrationsResult {
  const [integrations, setIntegrations] = useState<IntegrationStatusInfo[]>([]);
  const [isLoading, setIsLoading] = useState(true);
  const [error, setError] = useState<string | undefined>();
  const isMounted = useRef(true);

  const fetchIntegrations = useCallback(async () => {
    try {
      const response = await apiFetch("/api/integrations");
      if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
      }
      const data = (await response.json()) as {
        integrations: IntegrationStatusInfo[];
      };
      if (isMounted.current) {
        setIntegrations(data.integrations);
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
    fetchIntegrations();

    const interval = setInterval(fetchIntegrations, POLL_INTERVAL_MS);
    return () => {
      isMounted.current = false;
      clearInterval(interval);
    };
  }, [fetchIntegrations]);

  const connect = useCallback(
    async (id: string, config: Record<string, unknown>) => {
      const response = await apiFetch("/api/integrations", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ id, config }),
      });
      if (!response.ok) {
        const data = (await response.json()) as { error?: string };
        throw new Error(data.error ?? `HTTP ${response.status}`);
      }
      await fetchIntegrations();
    },
    [fetchIntegrations]
  );

  const disconnect = useCallback(
    async (id: string) => {
      const response = await apiFetch(
        `/api/integrations?id=${encodeURIComponent(id)}`,
        { method: "DELETE" }
      );
      if (!response.ok) {
        const data = (await response.json()) as { error?: string };
        throw new Error(data.error ?? `HTTP ${response.status}`);
      }
      await fetchIntegrations();
    },
    [fetchIntegrations]
  );

  return {
    integrations,
    isLoading,
    error,
    connect,
    disconnect,
    refetch: fetchIntegrations,
  };
}
