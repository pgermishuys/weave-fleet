import { describe, expect, it } from "vitest";
import { Github } from "lucide-react";
import type { FleetPluginManifest } from "@/plugins/types";
import {
  getContextResolvers,
  getRoutes,
  getSessionSourceContributions,
  getSettingsSections,
  getSidebarPanels,
  getSidebarViews,
  getStartupHooks,
} from "@/plugins/slots";

function makeManifest(id: string, order: number): FleetPluginManifest {
  return {
    descriptor: {
      id,
      displayName: id,
      trustLevel: "built-in",
      hasFrontend: true,
      hasBackend: false,
    },
    contributions: {
      sidebarItems: [
        {
          viewId: id,
          label: id,
          icon: Github,
          defaultPath: `/${id}`,
          order,
        },
      ],
      sidebarPanels: [
        {
          viewId: id,
          component: () => null,
          order,
        },
      ],
      routes: [
        {
          pluginId: id,
          path: `/${id}`,
          viewId: id,
          element: null,
        },
      ],
      settingsSections: [
        {
          id: `${id}-settings`,
          title: id,
          component: () => null,
          order,
        },
      ],
      startupHooks: [
        {
          id: `${id}-startup`,
          component: () => null,
          order,
        },
      ],
      contextResolvers: [
        {
          id: `${id}-context`,
          resolveContext: async () => null,
        },
      ],
      sessionSources: [
        {
          id: `${id}-source`,
          sourceKey: {
            providerId: id,
            sourceType: "custom",
          },
          label: `${id} source`,
          order,
          formComponent: () => null,
        },
      ],
    },
  };
}

describe("plugin slots", () => {
  const manifests = [makeManifest("beta", 20), makeManifest("alpha", 10)];

  it("orders sidebar views and panels by contribution order", () => {
    expect(getSidebarViews(manifests).map((item) => item.pluginId)).toEqual(["alpha", "beta"]);
    expect(getSidebarPanels(manifests).map((item) => item.pluginId)).toEqual(["alpha", "beta"]);
  });

  it("returns routes with plugin ids", () => {
    expect(getRoutes(manifests).map((route) => route.pluginId)).toEqual(["beta", "alpha"]);
  });

  it("orders settings sections and startup hooks", () => {
    expect(getSettingsSections(manifests).map((item) => item.pluginId)).toEqual(["alpha", "beta"]);
    expect(getStartupHooks(manifests).map((item) => item.pluginId)).toEqual(["alpha", "beta"]);
  });

  it("returns context resolvers and orders session source contributions", () => {
    expect(getContextResolvers(manifests).map((item) => item.pluginId)).toEqual(["beta", "alpha"]);
    expect(getSessionSourceContributions(manifests).map((item) => item.pluginId)).toEqual(["alpha", "beta"]);
  });
});
