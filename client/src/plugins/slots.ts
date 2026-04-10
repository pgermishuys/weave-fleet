import { getPlugins } from "./registry";
import type {
  FleetPluginContextResolver,
  FleetPluginManifest,
  FleetPluginRoute,
  FleetPluginSessionSourceContribution,
  FleetPluginSettingsSection,
  FleetPluginSidebarItem,
  FleetPluginSidebarPanel,
  FleetPluginStartupHook,
} from "./types";

export type RegisteredSidebarItem = FleetPluginSidebarItem & { pluginId: string };
export type RegisteredSidebarPanel = FleetPluginSidebarPanel & { pluginId: string };
export type RegisteredRoute = FleetPluginRoute & { pluginId: string };
export type RegisteredSettingsSection = FleetPluginSettingsSection & { pluginId: string };
export type RegisteredStartupHook = FleetPluginStartupHook & { pluginId: string };
export type RegisteredContextResolver = FleetPluginContextResolver & { pluginId: string };
export type RegisteredSessionSourceContribution = FleetPluginSessionSourceContribution & { pluginId: string };

function sortByOrder<T>(items: readonly T[], getOrder: (item: T) => number | undefined): T[] {
  return [...items].sort((left, right) => (getOrder(left) ?? 0) - (getOrder(right) ?? 0));
}

function getSourceManifests(manifests?: readonly FleetPluginManifest[]): readonly FleetPluginManifest[] {
  return manifests ?? getPlugins();
}

export function getSidebarViews(manifests?: readonly FleetPluginManifest[]): RegisteredSidebarItem[] {
  return sortByOrder(
    getSourceManifests(manifests).flatMap((manifest) =>
      (manifest.contributions?.sidebarItems ?? []).map((item) => ({
        ...item,
        pluginId: manifest.descriptor.id,
      }))
    ),
    (item) => item.order
  );
}

export function getSidebarPanels(manifests?: readonly FleetPluginManifest[]): RegisteredSidebarPanel[] {
  return sortByOrder(
    getSourceManifests(manifests).flatMap((manifest) =>
      (manifest.contributions?.sidebarPanels ?? []).map((panel) => ({
        ...panel,
        pluginId: manifest.descriptor.id,
      }))
    ),
    (panel) => panel.order
  );
}

export function getRoutes(manifests?: readonly FleetPluginManifest[]): RegisteredRoute[] {
  return getSourceManifests(manifests).flatMap((manifest) =>
    (manifest.contributions?.routes ?? []).map((route) => ({
      ...route,
      pluginId: manifest.descriptor.id,
    }))
  );
}

export function getSettingsSections(manifests?: readonly FleetPluginManifest[]): RegisteredSettingsSection[] {
  return sortByOrder(
    getSourceManifests(manifests).flatMap((manifest) =>
      (manifest.contributions?.settingsSections ?? []).map((section) => ({
        ...section,
        pluginId: manifest.descriptor.id,
      }))
    ),
    (section) => section.order
  );
}

export function getStartupHooks(manifests?: readonly FleetPluginManifest[]): RegisteredStartupHook[] {
  return sortByOrder(
    getSourceManifests(manifests).flatMap((manifest) =>
      (manifest.contributions?.startupHooks ?? []).map((hook) => ({
        ...hook,
        pluginId: manifest.descriptor.id,
      }))
    ),
    (hook) => hook.order
  );
}

export function getContextResolvers(manifests?: readonly FleetPluginManifest[]): RegisteredContextResolver[] {
  return getSourceManifests(manifests).flatMap((manifest) =>
    (manifest.contributions?.contextResolvers ?? []).map((resolver) => ({
      ...resolver,
      pluginId: manifest.descriptor.id,
    }))
  );
}

export function getSessionSourceContributions(manifests?: readonly FleetPluginManifest[]): RegisteredSessionSourceContribution[] {
  return sortByOrder(
    getSourceManifests(manifests).flatMap((manifest) =>
      (manifest.contributions?.sessionSources ?? []).map((source) => ({
        ...source,
        pluginId: manifest.descriptor.id,
      }))
    ),
    (source) => source.order
  );
}
