
import { createContext, useContext, useMemo } from "react";
import type { ReactNode } from "react";
import type { IntegrationStatusInfo } from "@/hooks/use-integrations";
import { usePluginRuntime } from "@/plugins/context";

/**
 * Transitional compatibility context.
 *
 * Plugin runtime state is canonical; this context preserves the old
 * integration-shaped API for callers that have not yet moved to plugin terms.
 */

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
  const {
    descriptors,
    statuses,
    isLoading,
    error,
    connect,
    disconnect,
    refetch,
  } = usePluginRuntime();

  const integrations = useMemo<IntegrationStatusInfo[]>(
    () =>
      descriptors.map((descriptor) => {
        const status = statuses.find((entry) => entry.pluginId === descriptor.id);
        return {
          id: descriptor.id,
          name: descriptor.displayName,
          status: status?.status ?? "disconnected",
          connectedAt: status?.connectedAt,
        };
      }),
    [descriptors, statuses]
  );

  const connectedIntegrations = useMemo(
    () => integrations.filter((i) => i.status === "connected"),
    [integrations]
  );

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
