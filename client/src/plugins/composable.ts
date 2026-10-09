import { computed, shallowRef, type ComputedRef } from "vue";
import { clearPlugins, pluginManifests, registerPlugin, registerPlugins } from "./registry";
import type { FleetPluginDescriptor, FleetPluginManifest, FleetPluginStatus } from "./types";

export interface PluginRuntimeComposable {
  manifests: ComputedRef<readonly FleetPluginManifest[]>;
  descriptors: ComputedRef<readonly FleetPluginDescriptor[]>;
  statuses: ComputedRef<readonly FleetPluginStatus[]>;
  isLoading: ComputedRef<boolean>;
  error: ComputedRef<string | undefined>;
  registerPlugin: (manifest: FleetPluginManifest) => void;
  registerPlugins: (manifests: readonly FleetPluginManifest[]) => void;
  clear: () => void;
  setStatuses: (statuses: readonly FleetPluginStatus[]) => void;
  setLoading: (isLoading: boolean) => void;
  setError: (error: string | undefined) => void;
  getStatus: (pluginId: string) => FleetPluginStatus | undefined;
}

// The plugins themselves live in the reactive registry; only what the server says about them is state here.
const statusesState = shallowRef<readonly FleetPluginStatus[]>([]);
const isLoadingState = shallowRef(false);
const errorState = shallowRef<string | undefined>(undefined);

const manifests = pluginManifests.items;

const descriptors = computed<readonly FleetPluginDescriptor[]>(() =>
  manifests.value.map((manifest) => manifest.descriptor)
);

const statuses = computed<readonly FleetPluginStatus[]>(() => statusesState.value);

const isLoading = computed<boolean>(() => isLoadingState.value);

const error = computed<string | undefined>(() => errorState.value);

function clear(): void {
  clearPlugins();
  statusesState.value = [];
  isLoadingState.value = false;
  errorState.value = undefined;
}

function setStatuses(statuses: readonly FleetPluginStatus[]): void {
  statusesState.value = statuses;
}

function setLoading(isLoading: boolean): void {
  isLoadingState.value = isLoading;
}

function setError(error: string | undefined): void {
  errorState.value = error;
}

function getStatus(pluginId: string): FleetPluginStatus | undefined {
  return statusesState.value.find((status) => status.pluginId === pluginId);
}

const runtime: PluginRuntimeComposable = {
  manifests,
  descriptors,
  statuses,
  isLoading,
  error,
  registerPlugin,
  registerPlugins,
  clear,
  setStatuses,
  setLoading,
  setError,
  getStatus,
};

export function usePluginRuntime(): PluginRuntimeComposable {
  return runtime;
}
