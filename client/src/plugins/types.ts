import type { Component } from "vue";

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

export interface FleetPluginContributions {
  sidebarItems?: readonly FleetPluginSidebarItem[];
  sidebarPanels?: readonly FleetPluginSidebarPanel[];
  settingsSections?: readonly FleetPluginSettingsSection[];
  configPage?: FleetPluginConfigPage;
}

export interface FleetPluginManifest {
  descriptor: FleetPluginDescriptor;
  contributions?: FleetPluginContributions;
}
