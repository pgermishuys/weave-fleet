import { computed, defineAsyncComponent, shallowRef, type Component, type ComputedRef } from "vue";
import { clearPlugins, getPlugins, registerPlugin as registerPluginInRegistry, registerPlugins as registerPluginsInRegistry } from "./registry";
import { getSessionSourceContributions, type RegisteredSessionSourceContribution } from "./slots";
import type { FleetPluginDescriptor, FleetPluginManifest, FleetPluginRenderProps, FleetPluginStatus } from "./types";

export interface PluginRuntimeComposable {
  manifests: ComputedRef<readonly FleetPluginManifest[]>;
  descriptors: ComputedRef<readonly FleetPluginDescriptor[]>;
  statuses: ComputedRef<readonly FleetPluginStatus[]>;
  sessionSources: ComputedRef<readonly RegisteredSessionSourceContribution[]>;
  isLoading: ComputedRef<boolean>;
  error: ComputedRef<string | undefined>;
  refresh: () => void;
  registerPlugin: (manifest: FleetPluginManifest) => void;
  registerPlugins: (manifests: readonly FleetPluginManifest[]) => void;
  clear: () => void;
  setStatuses: (statuses: readonly FleetPluginStatus[]) => void;
  setLoading: (isLoading: boolean) => void;
  setError: (error: string | undefined) => void;
  getStatus: (pluginId: string) => FleetPluginStatus | undefined;
}

export type FleetPluginAsyncComponentModule = {
  default: Component<FleetPluginRenderProps>;
};

export type FleetPluginAsyncComponentLoader = () => Promise<
  Component<FleetPluginRenderProps> | FleetPluginAsyncComponentModule
>;

const manifestsState = shallowRef<readonly FleetPluginManifest[]>(getPlugins());
const statusesState = shallowRef<readonly FleetPluginStatus[]>([]);
const isLoadingState = shallowRef(false);
const errorState = shallowRef<string | undefined>(undefined);

const descriptors = computed<readonly FleetPluginDescriptor[]>(() =>
  manifestsState.value.map((manifest) => manifest.descriptor)
);

const manifests = computed<readonly FleetPluginManifest[]>(() => manifestsState.value);

const statuses = computed<readonly FleetPluginStatus[]>(() => statusesState.value);

const isLoading = computed<boolean>(() => isLoadingState.value);

const error = computed<string | undefined>(() => errorState.value);

const sessionSources = computed<readonly RegisteredSessionSourceContribution[]>(() =>
  getSessionSourceContributions(manifestsState.value)
);

function isAsyncComponentModule(
  loaded: Component<FleetPluginRenderProps> | FleetPluginAsyncComponentModule
): loaded is FleetPluginAsyncComponentModule {
  return typeof loaded === "object" && loaded !== null && "default" in loaded;
}

function refresh(): void {
  manifestsState.value = getPlugins();
}

function registerPlugin(manifest: FleetPluginManifest): void {
  registerPluginInRegistry(manifest);
  refresh();
}

function registerPlugins(manifests: readonly FleetPluginManifest[]): void {
  registerPluginsInRegistry(manifests);
  refresh();
}

function clear(): void {
  clearPlugins();
  manifestsState.value = [];
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
  sessionSources,
  isLoading,
  error,
  refresh,
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

export function defineAsyncPluginComponent(loader: FleetPluginAsyncComponentLoader): Component<FleetPluginRenderProps> {
  return defineAsyncComponent(async () => {
    const loaded = await loader();

    return isAsyncComponentModule(loaded) ? loaded.default : loaded;
  });
}
