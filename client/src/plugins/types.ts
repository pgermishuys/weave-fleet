import type { ComponentType, LazyExoticComponent, ReactNode } from "react";
import type { RouteObject } from "react-router";
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
  icon: ComponentType<{ className?: string; size?: number }>;
  defaultPath: string;
  order?: number;
}

export interface FleetPluginSidebarPanel {
  viewId: FleetPluginViewId;
  component: ComponentType;
  order?: number;
}

export type FleetPluginRoute = RouteObject & {
  pluginId: string;
  viewId?: FleetPluginViewId;
};

export interface FleetPluginSettingsSection {
  id: string;
  title: string;
  component: ComponentType;
  icon?: ComponentType<{ size?: number; className?: string }>;
  order?: number;
}

export interface FleetPluginStartupHook {
  id: string;
  component: ComponentType;
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
  icon?: ComponentType<{ className?: string; size?: number }>;
  order?: number;
  formComponent?: ComponentType<FleetPluginSessionSourceFormProps>;
}

export interface FleetPluginContributions {
  sidebarItems?: readonly FleetPluginSidebarItem[];
  sidebarPanels?: readonly FleetPluginSidebarPanel[];
  routes?: readonly FleetPluginRoute[];
  settingsSections?: readonly FleetPluginSettingsSection[];
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
  | ReactNode
  | ComponentType<FleetPluginRenderProps>
  | LazyExoticComponent<ComponentType<FleetPluginRenderProps>>;
