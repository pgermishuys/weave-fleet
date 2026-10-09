import { computed, defineComponent, h } from "vue";
import { beforeEach, describe, expect, it } from "vitest";
import { usePluginRuntime } from "@/plugins/composable";
import {
  getConfigPage,
  getSettingsSections,
  getSidebarPanels,
  getSidebarViews,
} from "@/plugins/slots";
import type { FleetPluginManifest } from "@/plugins/types";

const Stub = (name: string) => defineComponent({ name, render: () => h("div", name) });

function manifest(id: string, contributions: FleetPluginManifest["contributions"] = {}): FleetPluginManifest {
  return {
    descriptor: { id, displayName: `Plugin ${id}`, trustLevel: "built-in", hasFrontend: true, hasBackend: false },
    contributions,
  };
}

describe("plugin runtime", () => {
  const runtime = usePluginRuntime();

  beforeEach(() => {
    runtime.clear();
  });

  it("lists registered plugins and their descriptors in registration order", () => {
    runtime.registerPlugins([manifest("alpha"), manifest("beta")]);
    runtime.registerPlugin(manifest("gamma"));

    expect(runtime.manifests.value.map((m) => m.descriptor.id)).toEqual(["alpha", "beta", "gamma"]);
    expect(runtime.descriptors.value.map((d) => d.displayName)).toEqual(["Plugin alpha", "Plugin beta", "Plugin gamma"]);
  });

  it("updates computeds that read the plugin list when a plugin registers", () => {
    const ids = computed(() => runtime.manifests.value.map((m) => m.descriptor.id));
    expect(ids.value).toEqual([]);

    runtime.registerPlugin(manifest("alpha"));

    expect(ids.value).toEqual(["alpha"]);
  });

  it("reads sidebar items sorted by order, tagged with the owning plugin", () => {
    const icon = Stub("Icon");
    runtime.registerPlugins([
      manifest("late", { sidebarItems: [{ viewId: "late", label: "Late", icon, defaultPath: "/late", order: 200 }] }),
      manifest("early", { sidebarItems: [{ viewId: "early", label: "Early", icon, defaultPath: "/early", order: 10 }] }),
      manifest("none", { sidebarItems: [{ viewId: "none", label: "None", icon, defaultPath: "/none" }] }),
    ]);

    const views = getSidebarViews();

    // A missing order counts as 0, so it sorts ahead of the numbered ones.
    expect(views.map((v) => v.viewId)).toEqual(["none", "early", "late"]);
    expect(views.map((v) => v.pluginId)).toEqual(["none", "early", "late"]);
  });

  it("reads sidebar panels sorted by order", () => {
    const first = Stub("First");
    const second = Stub("Second");
    runtime.registerPlugins([
      manifest("b", { sidebarPanels: [{ viewId: "b", component: second, order: 5 }] }),
      manifest("a", { sidebarPanels: [{ viewId: "a", component: first, order: 1 }] }),
    ]);

    const panels = getSidebarPanels();

    expect(panels.map((p) => p.viewId)).toEqual(["a", "b"]);
    expect(panels[0].component).toBe(first);
    expect(panels[0].pluginId).toBe("a");
  });

  it("keeps registration order for contributions with equal order", () => {
    runtime.registerPlugins([
      manifest("one", { sidebarPanels: [{ viewId: "one", component: Stub("One"), order: 3 }] }),
      manifest("two", { sidebarPanels: [{ viewId: "two", component: Stub("Two"), order: 3 }] }),
    ]);

    expect(getSidebarPanels().map((p) => p.viewId)).toEqual(["one", "two"]);
  });

  it("reads settings sections sorted by order", () => {
    runtime.registerPlugins([
      manifest("b", { settingsSections: [{ id: "b-sec", title: "B", component: Stub("B"), order: 2 }] }),
      manifest("a", { settingsSections: [{ id: "a-sec", title: "A", component: Stub("A"), order: 1 }] }),
    ]);

    const sections = getSettingsSections();

    expect(sections.map((s) => s.id)).toEqual(["a-sec", "b-sec"]);
    expect(sections.map((s) => s.pluginId)).toEqual(["a", "b"]);
  });

  it("reads one plugin's config page by plugin id", () => {
    const component = Stub("Config");
    runtime.registerPlugins([
      manifest("with", { configPage: { title: "Configure", component } }),
      manifest("without"),
    ]);

    expect(getConfigPage("with")).toMatchObject({ title: "Configure", component, pluginId: "with" });
    expect(getConfigPage("without")).toBeUndefined();
    expect(getConfigPage("missing")).toBeUndefined();
  });

  it("lets a plugin register without any contributions", () => {
    runtime.registerPlugin({ descriptor: manifest("bare").descriptor });

    expect(getSidebarViews()).toEqual([]);
    expect(getSidebarPanels()).toEqual([]);
    expect(getSettingsSections()).toEqual([]);
  });

  describe("duplicate plugin ids", () => {
    it("replaces the earlier plugin, keeps its place in the list and drops its contributions", () => {
      runtime.registerPlugins([
        manifest("dup", { sidebarPanels: [{ viewId: "old", component: Stub("Old"), order: 1 }] }),
        manifest("other"),
      ]);

      runtime.registerPlugin(manifest("dup", { sidebarPanels: [{ viewId: "new", component: Stub("New"), order: 1 }] }));

      expect(runtime.manifests.value.map((m) => m.descriptor.id)).toEqual(["dup", "other"]);
      expect(getSidebarPanels().map((p) => p.viewId)).toEqual(["new"]);
    });
  });

  it("clear() forgets plugins, statuses, loading and error", () => {
    runtime.registerPlugin(manifest("alpha"));
    runtime.setStatuses([{ pluginId: "alpha", status: "connected" }]);
    runtime.setLoading(true);
    runtime.setError("boom");

    runtime.clear();

    expect(runtime.manifests.value).toEqual([]);
    expect(runtime.statuses.value).toEqual([]);
    expect(runtime.isLoading.value).toBe(false);
    expect(runtime.error.value).toBeUndefined();
    expect(getSidebarPanels()).toEqual([]);
  });

  it("looks a status up by plugin id", () => {
    runtime.setStatuses([{ pluginId: "alpha", status: "connected" }, { pluginId: "beta", status: "error" }]);

    expect(runtime.getStatus("beta")?.status).toBe("error");
    expect(runtime.getStatus("nope")).toBeUndefined();
  });
});
