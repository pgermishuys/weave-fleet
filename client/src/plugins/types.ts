import type { Component, Ref } from "vue";

export type FleetPluginTrustLevel = "built-in";

export type FleetPluginViewId = string;

export interface FleetPluginDescriptor {
  id: string;
  displayName: string;
  trustLevel: FleetPluginTrustLevel;
  hasFrontend: boolean;
  hasBackend: boolean;
  /** One line under the name on the Plugins page. */
  description?: string;
  /** The plugin has no switch and is left off the Plugins page: Fleet's own way of reaching the page. */
  alwaysOn?: boolean;
  /** The plugin has no switch of its own: its page holds the switches (Built-in skills, one per skill). */
  switchless?: boolean;
}

/**
 * A plugin whose on/off state already lives somewhere else (a feature preference older than the Plugins page). The
 * Plugins page reads and flips that one, so there is never a second switch that can disagree with it.
 */
export interface FleetPluginOwnSwitch {
  /** Reactive: read inside a computed or a template. */
  isOn: () => boolean;
  /** Turns it on or off. May ask the user something first and leave it as it was. */
  set: (on: boolean) => Promise<void>;
  saving?: () => boolean;
  /** True while the switch waits for an answer shown beside it (Mods asking to install Bun). */
  pending?: () => boolean;
  /** Called when a Plugins page showing this switch opens, to read what it needs. */
  load?: () => void;
  /** Drawn under the plugin's row and at the top of its page: what the switch needs and its progress. */
  panel?: Component;
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
  /** The route the rail opens; leave it out for a rail that only switches the side panel. */
  defaultPath?: string;
  /** Where the icon sits in the icon rail. Defaults to the plugin group in the middle. */
  placement?: "plugin" | "bottom";
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
  /** The entry's name under Plugins in Settings; the title when absent. */
  navLabel?: string;
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
  /** Leave out for the usual switch (the server's list of plugins that are off). */
  ownSwitch?: FleetPluginOwnSwitch;
  contributions?: FleetPluginContributions;
}
