"use client";

import { createContext, useContext, useMemo, useEffect } from "react";
import type { ReactNode } from "react";
import { useIntegrations } from "@/hooks/use-integrations";
import type { IntegrationStatusInfo } from "@/hooks/use-integrations";

// Side-effect import — registers the GitHub integration in the registry
import "@/integrations/github";
import { setGitHubConfigured } from "@/integrations/github/manifest";

export interface IntegrationsContextValue {
  integrations: IntegrationStatusInfo[];
  connectedIntegrations: IntegrationStatusInfo[];
  isLoading: boolean;
  error?: string;
  connect: (id: string, config: Record<string, unknown>) => Promise<void>;
  disconnect: (id: string) => Promise<void>;
  refetch: () => void;
}

const defaultValue: IntegrationsContextValue = {
  integrations: [],
  connectedIntegrations: [],
  isLoading: true,
  error: undefined,
  connect: async () => {},
  disconnect: async () => {},
  refetch: () => {},
};

const IntegrationsContext =
  createContext<IntegrationsContextValue>(defaultValue);

export function IntegrationsProvider({ children }: { children: ReactNode }) {
  const { integrations, isLoading, error, connect, disconnect, refetch } =
    useIntegrations();

  const connectedIntegrations = useMemo(
    () => integrations.filter((i) => i.status === "connected"),
    [integrations]
  );

  // Keep manifest isConfigured() in sync with polling results
  useEffect(() => {
    const isGitHubConnected = integrations.some(
      (i) => i.id === "github" && i.status === "connected"
    );
    setGitHubConfigured(isGitHubConnected);
  }, [integrations]);

  const value = useMemo(
    () => ({
      integrations,
      connectedIntegrations,
      isLoading,
      error,
      connect,
      disconnect,
      refetch,
    }),
    [integrations, connectedIntegrations, isLoading, error, connect, disconnect, refetch]
  );

  return (
    <IntegrationsContext.Provider value={value}>
      {children}
    </IntegrationsContext.Provider>
  );
}

export function useIntegrationsContext(): IntegrationsContextValue {
  return useContext(IntegrationsContext);
}
