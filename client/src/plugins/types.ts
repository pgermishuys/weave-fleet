import type { RouteOptions } from "@tanstack/vue-router";
import type { Component, VNodeChild } from "vue";
import type { ContextSource } from "@/integrations/types";

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

type TanStackPluginRouteDefinition = RouteOptions<
  any,
  any,
  any,
  any,
  any,
  any,
  any,
  any,
  any,
  any,
  any,
  any,
  any,
  any,
  any
>;

export type FleetPluginRoute = TanStackPluginRouteDefinition & {
  pluginId: string;
  viewId?: FleetPluginViewId;
};

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

export interface FleetPluginStartupHook {
  id: string;
  component: Component;
  order?: number;
}

export interface FleetPluginContextResolver {
  id: string;
  resolveContext: (url: string) => Promise<ContextSource | null>;
}

export interface FleetPluginSessionSourceKey {
  providerId: string;
  sourceType: string;
}

export interface FleetPluginSessionSourceFormProps {
  providerId: string;
  sourceType: string;
}

export interface FleetPluginSessionSourceContribution {
  id: string;
  sourceKey: FleetPluginSessionSourceKey;
  label?: string;
  description?: string;
  icon?: Component;
  order?: number;
  formComponent?: Component;
}

export interface FleetPluginContributions {
  sidebarItems?: readonly FleetPluginSidebarItem[];
  sidebarPanels?: readonly FleetPluginSidebarPanel[];
  routes?: readonly FleetPluginRoute[];
  settingsSections?: readonly FleetPluginSettingsSection[];
  configPage?: FleetPluginConfigPage;
  startupHooks?: readonly FleetPluginStartupHook[];
  contextResolvers?: readonly FleetPluginContextResolver[];
  sessionSources?: readonly FleetPluginSessionSourceContribution[];
}

export interface FleetPluginManifest {
  descriptor: FleetPluginDescriptor;
  contributions?: FleetPluginContributions;
}

export type FleetBuiltInPluginModule = {
  readonly manifest: FleetPluginManifest;
};

export interface FleetPluginRenderProps {
  descriptor: FleetPluginDescriptor;
  status?: FleetPluginStatus;
}

export type FleetPluginRenderable =
  | VNodeChild
  | Component;
