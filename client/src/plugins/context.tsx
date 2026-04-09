import { createContext, useContext, useEffect, useMemo, type ReactNode } from "react";
import { useIntegrations } from "@/hooks/use-integrations";
import { setGitHubConfigured } from "@/integrations/github/manifest";
import { loadBuiltInPlugins } from "./loader";
import { createPluginRuntime } from "./runtime";
import type { FleetPluginRuntime } from "./runtime";
import type { PluginConnectionStatus } from "./types";

export interface PluginRuntimeContextValue extends FleetPluginRuntime {
  isLoading: boolean;
  error?: string;
  connect: (id: string, config: Record<string, unknown>) => Promise<void>;
  disconnect: (id: string) => Promise<void>;
  refetch: () => void;
}

const defaultValue: PluginRuntimeContextValue = {
  manifests: [],
  descriptors: [],
  statuses: [],
  isLoading: true,
  error: undefined,
  connect: async () => {},
  disconnect: async () => {},
  refetch: () => {},
};

const PluginRuntimeContext = createContext<PluginRuntimeContextValue>(defaultValue);

export function PluginRuntimeProvider({ children }: { children: ReactNode }) {
  const {
    pluginStatuses,
    isLoading,
    error,
    connect,
    disconnect,
    refetch,
  } = useIntegrations();
  const manifests = useMemo(() => loadBuiltInPlugins(), []);
  const statuses = useMemo(() => {
    const statusByPluginId = new Map(
      pluginStatuses.map((status) => [status.pluginId, status])
    );

    return manifests.map((manifest) =>
      statusByPluginId.get(manifest.descriptor.id) ?? {
        pluginId: manifest.descriptor.id,
        status: "disconnected" as PluginConnectionStatus,
        actions: [],
      }
    );
  }, [manifests, pluginStatuses]);
  const runtime = useMemo(() => createPluginRuntime(manifests, statuses), [manifests, statuses]);

  useEffect(() => {
    const isGitHubConnected = statuses.some(
      (status) => status.pluginId === "github" && status.status === "connected"
    );
    setGitHubConfigured(isGitHubConnected);
  }, [statuses]);

  const value = useMemo(
    () => ({
      ...runtime,
      isLoading,
      error,
      connect,
      disconnect,
      refetch,
    }),
    [connect, disconnect, error, isLoading, refetch, runtime]
  );

  return (
    <PluginRuntimeContext.Provider value={value}>
      {children}
    </PluginRuntimeContext.Provider>
  );
}

export function usePluginRuntime(): PluginRuntimeContextValue {
  return useContext(PluginRuntimeContext);
}
