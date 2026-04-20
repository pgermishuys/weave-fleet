import { onMounted, onUnmounted, readonly, ref, shallowRef, type Ref, type ShallowRef } from "vue";
import { apiFetch } from "@/lib/api-client";
import type { IntegrationStatusInfo, PluginCatalogResponse } from "@/lib/api-types";
import type {
  FleetPluginDescriptor,
  FleetPluginStatus,
  PluginActionDescriptor,
} from "@/plugins/types";

export type { IntegrationStatusInfo };

export interface UseIntegrationsResult {
  integrations: Readonly<Ref<readonly IntegrationStatusInfo[]>>;
  pluginDescriptors: Readonly<Ref<readonly FleetPluginDescriptor[]>>;
  pluginStatuses: Readonly<Ref<readonly FleetPluginStatus[]>>;
  isLoading: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | undefined>>;
  connect: (id: string, config: Record<string, unknown>) => Promise<void>;
  disconnect: (id: string) => Promise<void>;
  refetch: () => Promise<void>;
}

const POLL_INTERVAL_MS = 30_000;

function findPluginAction(
  pluginId: string,
  actionId: string,
  pluginStatuses: readonly FleetPluginStatus[],
): PluginActionDescriptor | undefined {
  return pluginStatuses
    .find((status) => status.pluginId === pluginId)
    ?.actions?.find((action) => action.id === actionId);
}

function toIntegrationStatuses(
  pluginDescriptors: readonly FleetPluginDescriptor[],
  pluginStatuses: readonly FleetPluginStatus[],
): IntegrationStatusInfo[] {
  const statusByPluginId = new Map(pluginStatuses.map((status) => [status.pluginId, status]));

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
  const integrations = ref<IntegrationStatusInfo[]>([]);
  const pluginDescriptors = ref<FleetPluginDescriptor[]>([]);
  const pluginStatuses = ref<FleetPluginStatus[]>([]);
  const isLoading = shallowRef(true);
  const error = shallowRef<string | undefined>(undefined);

  let timer: ReturnType<typeof setInterval> | undefined;
  let requestId = 0;

  async function fetchIntegrations(): Promise<void> {
    const currentRequestId = ++requestId;

    try {
      const response = await apiFetch("/api/plugins");
      if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
      }

      const data = (await response.json()) as PluginCatalogResponse;
      if (currentRequestId !== requestId) {
        return;
      }

      pluginDescriptors.value = data.plugins;
      pluginStatuses.value = data.statuses;
      integrations.value = toIntegrationStatuses(data.plugins, data.statuses);
      error.value = undefined;
    } catch (fetchError) {
      if (currentRequestId !== requestId) {
        return;
      }

      error.value = fetchError instanceof Error ? fetchError.message : String(fetchError);
    } finally {
      if (currentRequestId === requestId) {
        isLoading.value = false;
      }
    }
  }

  async function connect(id: string, config: Record<string, unknown>): Promise<void> {
    const action = findPluginAction(id, "connect", pluginStatuses.value);
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
  }

  async function disconnect(id: string): Promise<void> {
    const action = findPluginAction(id, "disconnect", pluginStatuses.value);
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
  }

  onMounted(() => {
    void fetchIntegrations();
    timer = setInterval(() => {
      void fetchIntegrations();
    }, POLL_INTERVAL_MS);
  });

  onUnmounted(() => {
    if (timer) {
      clearInterval(timer);
      timer = undefined;
    }
  });

  return {
    integrations: readonly(integrations),
    pluginDescriptors: readonly(pluginDescriptors),
    pluginStatuses: readonly(pluginStatuses),
    isLoading: readonly(isLoading),
    error: readonly(error),
    connect,
    disconnect,
    refetch: fetchIntegrations,
  };
}
