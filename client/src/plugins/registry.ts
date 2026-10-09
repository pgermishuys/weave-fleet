import { defineContributionPoint } from "@/lib/contributions";
import type {
  FleetPluginConfigPage,
  FleetPluginManifest,
  FleetPluginSettingsSection,
  FleetPluginSidebarItem,
  FleetPluginSidebarPanel,
} from "./types";

export type RegisteredSidebarItem = FleetPluginSidebarItem & { pluginId: string };
export type RegisteredSidebarPanel = FleetPluginSidebarPanel & { pluginId: string };
export type RegisteredSettingsSection = FleetPluginSettingsSection & { pluginId: string };
export type RegisteredConfigPage = FleetPluginConfigPage & { pluginId: string };

// A plugin's contributions are owned by its id. Within each point ids are unique and a later one replaces an
// earlier one (see lib/contributions.ts), so registering a plugin id again replaces that plugin whole.
export const pluginManifests = defineContributionPoint<FleetPluginManifest>({
  name: "plugins",
  idOf: (manifest) => manifest.descriptor.id,
});

export const sidebarItems = defineContributionPoint<RegisteredSidebarItem>({
  name: "plugin sidebar items",
  idOf: (item) => item.viewId,
});

export const sidebarPanels = defineContributionPoint<RegisteredSidebarPanel>({
  name: "plugin sidebar panels",
  idOf: (panel) => panel.viewId,
});

export const settingsSections = defineContributionPoint<RegisteredSettingsSection>({
  name: "plugin settings sections",
  idOf: (section) => section.id,
  labelOf: (section) => section.title,
});

export const configPages = defineContributionPoint<RegisteredConfigPage>({
  name: "plugin config pages",
  idOf: (page) => page.pluginId,
});

const withPluginId = <T extends object>(pluginId: string, items: readonly T[] | undefined) =>
  (items ?? []).map((item) => ({ ...item, pluginId }));

export function registerPlugin(manifest: FleetPluginManifest): void {
  const pluginId = manifest.descriptor.id;
  const contributions = manifest.contributions;

  // Registering an id again replaces the plugin, so what the old one contributed goes first.
  removePlugin(pluginId);

  pluginManifests.contribute(pluginId, [manifest]);
  sidebarItems.contribute(pluginId, withPluginId(pluginId, contributions?.sidebarItems));
  sidebarPanels.contribute(pluginId, withPluginId(pluginId, contributions?.sidebarPanels));
  settingsSections.contribute(pluginId, withPluginId(pluginId, contributions?.settingsSections));
  if (contributions?.configPage) {
    configPages.contribute(pluginId, withPluginId(pluginId, [contributions.configPage]));
  }
}

export function registerPlugins(manifests: readonly FleetPluginManifest[]): void {
  for (const manifest of manifests) {
    registerPlugin(manifest);
  }
}

function removePlugin(pluginId: string): void {
  for (const point of [sidebarItems, sidebarPanels, settingsSections, configPages]) {
    point.removeByOwner(pluginId);
  }
}

export function clearPlugins(): void {
  for (const point of [pluginManifests, sidebarItems, sidebarPanels, settingsSections, configPages]) {
    point.clear();
  }
}
