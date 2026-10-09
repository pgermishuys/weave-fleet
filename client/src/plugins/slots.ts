import type { FleetPluginBoardSource, FleetPluginRepositorySource } from "./types";
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

export function getConfigPage(pluginId: string): RegisteredConfigPage | undefined {
  return configPages.get(pluginId);
}

export function getRepositorySources(): readonly FleetPluginRepositorySource[] {
  return repositorySources.items.value;
}

export function getBoardSources(): readonly FleetPluginBoardSource[] {
  return boardSources.items.value;
}
