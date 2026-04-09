
import { useState, useEffect, useCallback, useRef } from "react";
import { apiFetch } from "@/lib/api-client";
import type {
  IntegrationStatusInfo,
  PluginCatalogResponse,
} from "@/lib/api-types";
import type {
  FleetPluginDescriptor,
  FleetPluginStatus,
  PluginActionDescriptor,
} from "@/plugins/types";

export type { IntegrationStatusInfo };

export interface UseIntegrationsResult {
  integrations: IntegrationStatusInfo[];
  pluginDescriptors: FleetPluginDescriptor[];
  pluginStatuses: FleetPluginStatus[];
  isLoading: boolean;
  error?: string;
  connect: (id: string, config: Record<string, unknown>) => Promise<void>;
  disconnect: (id: string) => Promise<void>;
  refetch: () => void;
}

const POLL_INTERVAL_MS = 30_000;

function findPluginAction(
  pluginId: string,
  actionId: string,
  pluginStatuses: readonly FleetPluginStatus[]
): PluginActionDescriptor | undefined {
  return pluginStatuses
    .find((status) => status.pluginId === pluginId)
    ?.actions?.find((action) => action.id === actionId);
}

function toIntegrationStatuses(
  pluginDescriptors: readonly FleetPluginDescriptor[],
  pluginStatuses: readonly FleetPluginStatus[]
): IntegrationStatusInfo[] {
  const statusByPluginId = new Map(
    pluginStatuses.map((status) => [status.pluginId, status])
  );

  return pluginDescriptors.map((descriptor) => {
    const status = statusByPluginId.get(descriptor.id);

    return {
      id: descriptor.id,
      name: descriptor.displayName,
      status: status?.status ?? "disconnected",
      connectedAt: status?.connectedAt,
    };
  });
}

export function useIntegrations(): UseIntegrationsResult {
  const [integrations, setIntegrations] = useState<IntegrationStatusInfo[]>([]);
  const [pluginDescriptors, setPluginDescriptors] = useState<FleetPluginDescriptor[]>([]);
  const [pluginStatuses, setPluginStatuses] = useState<FleetPluginStatus[]>([]);
  const [isLoading, setIsLoading] = useState(true);
  const [error, setError] = useState<string | undefined>();
  const isMounted = useRef(true);

  const fetchIntegrations = useCallback(async () => {
    try {
      const response = await apiFetch("/api/plugins");
      if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
      }
      const data = (await response.json()) as PluginCatalogResponse;
      if (isMounted.current) {
        setPluginDescriptors(data.plugins);
        setPluginStatuses(data.statuses);
        setIntegrations(toIntegrationStatuses(data.plugins, data.statuses));
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
      const action = findPluginAction(id, "connect", pluginStatuses);
      if (!action?.href) {
        throw new Error(`No connect action available for plugin '${id}'.`);
      }

      const response = await apiFetch(action.href, {
        method: action.method ?? "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(config),
      });
      if (!response.ok) {
        const data = (await response.json()) as { error?: string };
        throw new Error(data.error ?? `HTTP ${response.status}`);
      }
      await fetchIntegrations();
    },
    [fetchIntegrations, pluginStatuses]
  );

  const disconnect = useCallback(
    async (id: string) => {
      const action = findPluginAction(id, "disconnect", pluginStatuses);
      if (!action?.href) {
        throw new Error(`No disconnect action available for plugin '${id}'.`);
      }

      const response = await apiFetch(action.href, {
        method: action.method ?? "DELETE",
      });
      if (!response.ok) {
        const data = (await response.json()) as { error?: string };
        throw new Error(data.error ?? `HTTP ${response.status}`);
      }
      await fetchIntegrations();
    },
    [fetchIntegrations, pluginStatuses]
  );

  return {
    integrations,
    pluginDescriptors,
    pluginStatuses,
    isLoading,
    error,
    connect,
    disconnect,
    refetch: fetchIntegrations,
  };
}
