import type { FleetPluginManifest } from "./types";

const manifests = new Map<string, FleetPluginManifest>();

export function registerPlugin(manifest: FleetPluginManifest): void {
  manifests.set(manifest.descriptor.id, manifest);
}

export function registerPlugins(pluginManifests: readonly FleetPluginManifest[]): void {
  for (const manifest of pluginManifests) {
    registerPlugin(manifest);
  }
}

export function clearPlugins(): void {
  manifests.clear();
}

export function getPlugin(pluginId: string): FleetPluginManifest | undefined {
  return manifests.get(pluginId);
}

export function getPlugins(): readonly FleetPluginManifest[] {
  return Array.from(manifests.values());
}
