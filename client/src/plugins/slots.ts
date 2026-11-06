import type { FleetPluginBoardSource, FleetPluginManifest, FleetPluginRepositorySource } from "./types";
import {
  boardSources,
  configPages,
  repositorySources,
  settingsSections,
  sidebarItems,
  sidebarPanels,
  type RegisteredConfigPage,
  type RegisteredSettingsSection,
  type RegisteredSidebarItem,
  type RegisteredSidebarPanel,
} from "./registry";

export { isPluginEnabled, setPluginEnabled } from "./registry";

export type {
  RegisteredConfigPage,
  RegisteredSettingsSection,
  RegisteredSidebarItem,
  RegisteredSidebarPanel,
} from "./registry";

// Every getter reads a reactive contribution point, so calling one inside a computed or a template keeps it
// up to date as plugins register.

export function getSidebarViews(): readonly RegisteredSidebarItem[] {
  return sidebarItems.items.value;
}

export function getSidebarPanels(): readonly RegisteredSidebarPanel[] {
  return sidebarPanels.items.value;
}

export function getSettingsSections(): readonly RegisteredSettingsSection[] {
  return settingsSections.items.value;
}

export function getConfigPages(): readonly RegisteredConfigPage[] {
  return configPages.items.value;
}

export function getConfigPage(pluginId: string): RegisteredConfigPage | undefined {
  return configPages.get(pluginId);
}

export function getRepositorySources(): readonly FleetPluginRepositorySource[] {
  return repositorySources.items.value;
}

export function getBoardSources(): readonly FleetPluginBoardSource[] {
  return boardSources.items.value;
}

/** One thing a plugin adds to Fleet, for the Plugins page. `point` names the contribution point it comes from. */
export interface PluginAddition {
  area: "Rail" | "Settings" | "Folder picker" | "Board";
  detail: string;
  point: string;
}

/** What a plugin adds, read from its contribution points, in the order Fleet shows the areas. */
export function describeContributions(manifest: FleetPluginManifest): readonly PluginAddition[] {
  const contributions = manifest.contributions;
  if (!contributions) return [];
  const added: PluginAddition[] = [];

  for (const item of contributions.sidebarItems ?? []) {
    added.push({ area: "Rail", detail: item.defaultPath ? `${item.label} (opens ${item.defaultPath})` : item.label, point: "sidebarItems" });
  }
  if (contributions.configPage) {
    added.push({ area: "Settings", detail: contributions.configPage.title, point: "configPage" });
  }
  for (const section of contributions.settingsSections ?? []) {
    added.push({ area: "Settings", detail: section.title, point: "settingsSections" });
  }
  for (const source of contributions.repositorySources ?? []) {
    added.push({ area: "Folder picker", detail: `Suggests your repositories to clone (${source.id})`, point: "repositorySources" });
  }
  for (const source of contributions.boardSources ?? []) {
    added.push({ area: "Board", detail: source.description, point: "boardSources" });
  }
  return added;
}
