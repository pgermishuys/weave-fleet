import type { IntegrationManifest } from "./types";
import { registerPlugin } from "@/plugins/registry";
import type { FleetPluginManifest } from "@/plugins/types";
import { getPlugins } from "@/plugins/registry";

/**
 * Transitional compatibility registry.
 *
 * Plugin registration is the source of truth. This module remains only so
 * legacy integration callers can read an integration-shaped projection while
 * the rest of the migration converges on plugin naming.
 */
const manifests = new Map<string, IntegrationManifest>();

export function createPluginManifestFromIntegration(
  manifest: IntegrationManifest
): FleetPluginManifest {
  return {
    descriptor: {
      id: manifest.id,
      displayName: manifest.name,
      trustLevel: manifest.pluginDescriptor?.trustLevel ?? "built-in",
      hasFrontend: true,
      hasBackend: manifest.pluginDescriptor?.hasBackend ?? false,
    },
    contributions: {
      settingsSections: manifest.settingsComponent
        ? [
            {
              id: `${manifest.id}-settings`,
              title: manifest.name,
              component: manifest.settingsComponent,
              icon: manifest.icon,
            },
          ]
        : undefined,
      contextResolvers: [
        {
          id: `${manifest.id}-context`,
          resolveContext: manifest.resolveContext,
        },
      ],
    },
  };
}

export function registerIntegrationCompatibility(manifest: IntegrationManifest): void {
  manifests.set(manifest.id, manifest);
}

export function registerIntegration(manifest: IntegrationManifest): void {
  registerIntegrationCompatibility(manifest);
  registerPlugin(createPluginManifestFromIntegration(manifest));
}

export function getIntegrations(): readonly IntegrationManifest[] {
  return getPlugins()
    .map((plugin) => manifests.get(plugin.descriptor.id))
    .filter((manifest): manifest is IntegrationManifest => manifest !== undefined);
}

export function getIntegration(id: string): IntegrationManifest | undefined {
  return manifests.get(id);
}

export function getConnectedIntegrations(): IntegrationManifest[] {
  return getIntegrations().filter((manifest) => manifest.isConfigured());
}
