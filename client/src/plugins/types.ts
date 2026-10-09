import type { Component, Ref } from "vue";

export type FleetPluginTrustLevel = "built-in";

export type FleetPluginViewId = string;

export interface FleetPluginDescriptor {
  id: string;
  displayName: string;
  trustLevel: FleetPluginTrustLevel;
  hasFrontend: boolean;
  hasBackend: boolean;
}

export type PluginConnectionStatus = "connected" | "disconnected" | "error";

export interface PluginActionDescriptor {
  id: string;
  label: string;
  href?: string;
  method?: "GET" | "POST" | "DELETE";
}

export interface FleetPluginStatus {
  pluginId: string;
  status: PluginConnectionStatus;
  connectedAt?: string;
  actions?: readonly PluginActionDescriptor[];
}

export interface FleetPluginSidebarItem {
  viewId: FleetPluginViewId;
  label: string;
  icon: Component;
  defaultPath: string;
  order?: number;
}

export interface FleetPluginSidebarPanel {
  viewId: FleetPluginViewId;
  component: Component;
  order?: number;
}

export interface FleetPluginSettingsSection {
  id: string;
  title: string;
  component: Component;
  icon?: Component;
  order?: number;
}

export interface FleetPluginConfigPage {
  title: string;
  component: Component;
  icon?: Component;
}

/** A repository a plugin can offer for cloning. */
export interface FleetPluginRepository {
  id: number | string;
  /** `owner/name`. */
  fullName: string;
  name: string;
}

/** What a repository source gives the new-session folder picker; read in a component's `setup`. */
export interface FleetPluginRepositoryList {
  repos: Readonly<Ref<readonly FleetPluginRepository[]>>;
  /** Loads (or reloads) the list. It must not load anything before this is called. */
  refresh: () => Promise<void>;
}

/** Repositories the folder picker's Clone view suggests. */
export interface FleetPluginRepositorySource {
  /** Drawn beside each of this source's suggestions. */
  icon: Component;
  id: string;
  /** Called in a component's `setup`, so it may use lifecycle hooks. */
  useRepositories: () => FleetPluginRepositoryList;
}

/** What a board source gives the board's source settings; read in a component's `setup`. */
export interface FleetPluginBoardRepositories {
  repositories: Readonly<Ref<readonly { fullName: string }[]>>;
  error: Readonly<Ref<string | null>>;
  isLoading: Readonly<Ref<boolean>>;
  refresh: () => Promise<void>;
}

/** Where a board can sync cards from. The board uses the first one contributed. */
export interface FleetPluginBoardSource {
  /** Also the provider type stored on a board source made from this one. */
  id: string;
  /** Under the Sources heading. */
  description: string;
  /** Shown when there is nothing to pick from. */
  emptyHint: string;
  /** Called in a component's `setup`, so it may use lifecycle hooks. */
  useRepositories: () => FleetPluginBoardRepositories;
}

export interface FleetPluginContributions {
  sidebarItems?: readonly FleetPluginSidebarItem[];
  sidebarPanels?: readonly FleetPluginSidebarPanel[];
  settingsSections?: readonly FleetPluginSettingsSection[];
  configPage?: FleetPluginConfigPage;
  repositorySources?: readonly FleetPluginRepositorySource[];
  boardSources?: readonly FleetPluginBoardSource[];
}

export interface FleetPluginManifest {
  descriptor: FleetPluginDescriptor;
  contributions?: FleetPluginContributions;
}
