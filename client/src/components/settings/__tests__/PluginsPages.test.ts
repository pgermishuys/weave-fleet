import { enableAutoUnmount, flushPromises, mount } from "@vue/test-utils";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { defineComponent, h } from "vue";

vi.mock("@/composables/use-fleet-updates", () => ({ useFleetUpdatesWatch: () => ({ waiting: { value: 0 } }) }));

const { registerPlugin, clearPlugins, setPluginEnabled } = await import("@/plugins/registry");
const { useSettingsNav } = await import("@/composables/use-settings-nav");
const { default: PluginsOverview } = await import("@/components/settings/PluginsOverview.vue");
const { default: PluginPage } = await import("@/components/settings/PluginPage.vue");

enableAutoUnmount(afterEach);

const Stub = defineComponent({ name: "Stub", render: () => h("div", { "data-testid": "own-settings" }, "own settings") });

beforeEach(() => {
  localStorage.clear();
  clearPlugins();
  setPluginEnabled("demo", true);
  registerPlugin({
    descriptor: { id: "demo", displayName: "Demo", trustLevel: "built-in", hasFrontend: true, hasBackend: false, description: "A demo plugin." },
    contributions: {
      sidebarItems: [{ viewId: "demo", label: "Demo", icon: Stub, defaultPath: "/demo" }],
      configPage: { title: "Demo settings", component: Stub },
      boardSources: [{ id: "demo", description: "Cards from Demo", emptyHint: "None", useRepositories: () => ({}) as never }],
    },
  });
  registerPlugin({
    descriptor: { id: "host", displayName: "Host", trustLevel: "built-in", hasFrontend: true, hasBackend: false, alwaysOn: true },
  });
});

describe("Settings › Plugins", () => {
  it("lists each plugin with its description and Adds to chips from its contribution points, leaving out alwaysOn ones", () => {
    const wrapper = mount(PluginsOverview);
    const row = wrapper.get("[data-plugin-row='demo']");
    expect(row.text()).toContain("A demo plugin.");
    expect(row.findAll("[data-testid='plugin-adds-chip']").map((c) => c.text())).toEqual(["Rail", "Settings", "Board"]);
    expect(wrapper.find("[data-plugin-row='host']").exists()).toBe(false);
  });

  it("switches a plugin off and on from its row", async () => {
    const wrapper = mount(PluginsOverview);
    const toggle = wrapper.get("[data-testid='plugin-switch-demo']");
    expect(toggle.attributes("aria-checked")).toBe("true");
    await toggle.trigger("click");
    expect(toggle.attributes("aria-checked")).toBe("false");
    await toggle.trigger("click");
    expect(toggle.attributes("aria-checked")).toBe("true");
  });

  it("opens a plugin's page from its name", async () => {
    const wrapper = mount(PluginsOverview);
    await wrapper.get("[data-plugin-row='demo'] button").trigger("click");
    expect(useSettingsNav().activeSection.value).toBe("plugin:demo");
  });

  it("shows a plugin's own settings and what it adds, and replaces the settings with a note when it is off", async () => {
    const wrapper = mount(PluginPage, { props: { pluginId: "demo" } });
    expect(wrapper.find("[data-testid='own-settings']").exists()).toBe(true);
    expect(wrapper.findAll("[data-testid='plugin-addition']")).toHaveLength(3);
    expect(wrapper.text()).toContain("Plugins");

    await wrapper.get("[data-testid='plugin-page-switch']").trigger("click");
    await flushPromises();
    expect(wrapper.find("[data-testid='own-settings']").exists()).toBe(false);
    expect(wrapper.find("[data-testid='plugin-off-note']").exists()).toBe(true);
  });

  it("says so when no plugin has that id", () => {
    expect(mount(PluginPage, { props: { pluginId: "missing" } }).find("[data-testid='plugin-not-found']").exists()).toBe(true);
  });
});
