import type {
  FleetPluginDescriptor,
  FleetPluginManifest,
  FleetPluginStatus,
} from "./types";

export interface FleetPluginRuntime {
  manifests: readonly FleetPluginManifest[];
  descriptors: readonly FleetPluginDescriptor[];
  statuses: readonly FleetPluginStatus[];
}

export interface FleetPluginRuntimeSnapshot {
  descriptors: readonly FleetPluginDescriptor[];
  statuses: readonly FleetPluginStatus[];
}

export function createPluginRuntime(
  manifests: readonly FleetPluginManifest[],
  statuses: readonly FleetPluginStatus[]
): FleetPluginRuntime {
  return {
    manifests,
    descriptors: manifests.map((manifest) => manifest.descriptor),
    statuses,
  };
}

export function getPluginStatus(
  runtime: FleetPluginRuntimeSnapshot,
  pluginId: string
): FleetPluginStatus | undefined {
  return runtime.statuses.find((status) => status.pluginId === pluginId);
}
