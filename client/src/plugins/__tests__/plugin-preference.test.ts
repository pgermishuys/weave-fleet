import { createPinia, setActivePinia } from "pinia";
import { defineComponent, h, nextTick } from "vue";
import { beforeEach, describe, expect, it, vi } from "vitest";

const api = vi.hoisted(() => ({
  stored: {} as Record<string, string>,
  GET: vi.fn(),
  PUT: vi.fn(),
}));
vi.mock("@/api/client", () => ({ api }));

const { usePluginRuntime } = await import("@/plugins/composable");
const { getSidebarViews, isPluginEnabled } = await import("@/plugins/slots");
const { DISABLED_PLUGINS_PREFERENCE_KEY, DISABLED_STORAGE_KEY, holdPluginsUntilKnown, setDisabledPlugins } = await import("@/plugins/registry");
const { setPluginEnabledEverywhere, usePluginPreferenceSync } = await import("@/plugins/use-plugin-switch");
const { usePreferencesStore } = await import("@/stores/preferences");

const Stub = defineComponent({ name: "StubPane", render: () => h("div") });
const demo = {
  descriptor: { id: "demo", displayName: "Demo", trustLevel: "built-in" as const, hasFrontend: true, hasBackend: false },
  contributions: { sidebarItems: [{ viewId: "demo", label: "Demo", icon: Stub }] },
};

async function settle() {
  for (let i = 0; i < 5; i++) await nextTick();
}

describe("plugin switch kept as a server preference", () => {
  const runtime = usePluginRuntime();

  beforeEach(() => {
    localStorage.clear();
    setActivePinia(createPinia());
    runtime.clear();
    setDisabledPlugins(new Set());
    api.stored = {};
    api.GET.mockImplementation(async () => ({ data: { ...api.stored } }));
    api.PUT.mockImplementation(async (_path: string, init: { params: { path: { key: string } }; body: { value: string } }) => {
      api.stored[init.params.path.key] = init.body.value;
      return {};
    });
  });

  it("saves the list of plugins that are off to the server when one is switched", async () => {
    runtime.registerPlugin(demo);
    await setPluginEnabledEverywhere("demo", false);
    expect(api.stored[DISABLED_PLUGINS_PREFERENCE_KEY]).toBe('["demo"]');
    expect(getSidebarViews()).toEqual([]);
    await setPluginEnabledEverywhere("demo", true);
    expect(api.stored[DISABLED_PLUGINS_PREFERENCE_KEY]).toBe("[]");
    expect(getSidebarViews()).toHaveLength(1);
  });

  it("takes the server's list when preferences arrive, on a browser that has never seen it", async () => {
    api.stored[DISABLED_PLUGINS_PREFERENCE_KEY] = '["demo"]';
    holdPluginsUntilKnown();
    runtime.registerPlugin(demo);
    // Nothing flashes in while the answer is on its way.
    expect(getSidebarViews()).toEqual([]);

    usePluginPreferenceSync();
    await settle();
    expect(isPluginEnabled("demo")).toBe(false);
    expect(getSidebarViews()).toEqual([]);
  });

  it("brings the plugin in once the server says it is on", async () => {
    api.stored[DISABLED_PLUGINS_PREFERENCE_KEY] = "[]";
    holdPluginsUntilKnown();
    runtime.registerPlugin(demo);
    expect(getSidebarViews()).toEqual([]);
    usePluginPreferenceSync();
    await settle();
    expect(getSidebarViews()).toHaveLength(1);
  });

  it("lets another device's change win over this browser's copy", async () => {
    localStorage.setItem(DISABLED_STORAGE_KEY, '["demo"]');
    setDisabledPlugins(new Set(["demo"]));
    runtime.registerPlugin(demo);
    api.stored[DISABLED_PLUGINS_PREFERENCE_KEY] = "[]";
    usePluginPreferenceSync();
    await settle();
    expect(isPluginEnabled("demo")).toBe(true);
    expect(getSidebarViews()).toHaveLength(1);
  });

  it("moves a list this browser had before the preference existed up to the server, once", async () => {
    localStorage.setItem(DISABLED_STORAGE_KEY, '["demo"]');
    setDisabledPlugins(new Set(["demo"]));
    runtime.registerPlugin(demo);
    usePluginPreferenceSync();
    await settle();
    expect(api.stored[DISABLED_PLUGINS_PREFERENCE_KEY]).toBe('["demo"]');
    expect(usePreferencesStore().get(DISABLED_PLUGINS_PREFERENCE_KEY, "")).toBe('["demo"]');
    expect(getSidebarViews()).toEqual([]);
  });

  it("shows plugins as on when the preferences can't be read", async () => {
    api.GET.mockRejectedValue(new Error("offline"));
    holdPluginsUntilKnown();
    runtime.registerPlugin(demo);
    usePluginPreferenceSync();
    await settle();
    expect(getSidebarViews()).toHaveLength(1);
  });
});
