import { defineComponent, h } from "vue";
import { beforeEach, describe, expect, it } from "vitest";
import { usePluginRuntime } from "@/plugins/composable";
import {
  describeContributions,
  getBoardSources,
  getConfigPage,
  getRepositorySources,
  getSidebarViews,
  isPluginEnabled,
  setPluginEnabled,
} from "@/plugins/slots";
import type { FleetPluginManifest } from "@/plugins/types";

const Stub = defineComponent({ name: "StubPane", render: () => h("div") });

const full: FleetPluginManifest = {
  descriptor: { id: "full", displayName: "Full", trustLevel: "built-in", hasFrontend: true, hasBackend: false },
  contributions: {
    sidebarItems: [{ viewId: "full", label: "Full", icon: Stub, defaultPath: "/full" }],
    configPage: { title: "Full settings", component: Stub },
    repositorySources: [{ id: "full", icon: Stub, useRepositories: () => ({ repos: { value: [] } as never, refresh: async () => {} }) }],
    boardSources: [{ id: "full", description: "Cards from Full", emptyHint: "None", useRepositories: () => ({}) as never }],
  },
};

describe("turning a plugin on and off", () => {
  const runtime = usePluginRuntime();

  beforeEach(() => {
    localStorage.clear();
    runtime.clear();
    setPluginEnabled("full", true);
    setPluginEnabled("pinned", true);
  });

  it("removes everything a plugin adds when it is off, and restores it when it is on", () => {
    runtime.registerPlugin(full);
    expect(getSidebarViews().map((v) => v.viewId)).toEqual(["full"]);
    expect(getConfigPage("full")).toBeDefined();
    expect(getRepositorySources()).toHaveLength(1);
    expect(getBoardSources()).toHaveLength(1);

    setPluginEnabled("full", false);
    expect(isPluginEnabled("full")).toBe(false);
    expect(getSidebarViews()).toEqual([]);
    expect(getConfigPage("full")).toBeUndefined();
    expect(getRepositorySources()).toEqual([]);
    expect(getBoardSources()).toEqual([]);
    // Still listed, so the Plugins page can switch it back on.
    expect(runtime.manifests.value.map((m) => m.descriptor.id)).toEqual(["full"]);

    setPluginEnabled("full", true);
    expect(getSidebarViews().map((v) => v.viewId)).toEqual(["full"]);
    expect(getConfigPage("full")).toBeDefined();
  });

  it("keeps the switch across reloads, and registers a plugin that is off without its contributions", async () => {
    setPluginEnabled("full", false);
    expect(JSON.parse(localStorage.getItem("fleet.plugins.disabled") ?? "[]")).toEqual(["full"]);
    runtime.registerPlugin(full);
    expect(getSidebarViews()).toEqual([]);
    expect(runtime.manifests.value).toHaveLength(1);
  });

  it("never switches off a plugin marked alwaysOn", () => {
    const pinned: FleetPluginManifest = {
      descriptor: { ...full.descriptor, id: "pinned", alwaysOn: true },
      contributions: { sidebarItems: [{ viewId: "pinned", label: "Pinned", icon: Stub }] },
    };
    runtime.registerPlugin(pinned);
    setPluginEnabled("pinned", false);
    expect(getSidebarViews().map((v) => v.viewId)).toEqual(["pinned"]);
  });

  it("describes what a plugin adds from its contribution points", () => {
    expect(describeContributions(full).map((a) => [a.area, a.point])).toEqual([
      ["Rail", "sidebarItems"],
      ["Settings", "configPage"],
      ["Folder picker", "repositorySources"],
      ["Board", "boardSources"],
    ]);
    expect(describeContributions({ descriptor: full.descriptor })).toEqual([]);
  });
});
