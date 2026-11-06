import { enableAutoUnmount, flushPromises, mount } from "@vue/test-utils";
import { afterEach, describe, expect, it, vi } from "vitest";
import { reactive, ref } from "vue";

const store = vi.hoisted(() => ({ state: null as unknown as { isSwitchedOn: boolean; loadSwitch: ReturnType<typeof vi.fn> } }));
vi.mock("@/stores/mods", () => ({ useModsStore: () => store.state }));

const updates = vi.hoisted(() => ({ enabled: { value: false }, waiting: { value: 0 } }));
vi.mock("@/composables/use-fleet-updates", () => ({
  useFleetUpdatesWatch: () => ({ enabled: ref(updates.enabled.value), waiting: ref(updates.waiting.value) }),
}));

const update = vi.hoisted(() => ({ available: { value: false } }));
vi.mock("@/composables/use-update-status", () => ({
  useUpdateStatus: () => ({ isUpdateAvailable: ref(update.available.value), isUpdateStaged: ref(false) }),
}));

const { registerPlugin, clearPlugins, setPluginEnabled } = await import("@/plugins/registry");
const { default: SettingsNavPanel } = await import("@/components/settings/SettingsNavPanel.vue");

enableAutoUnmount(afterEach);

async function mountNav(on: boolean) {
  store.state = reactive({ isSwitchedOn: on, loadSwitch: vi.fn().mockResolvedValue(undefined) });
  const wrapper = mount(SettingsNavPanel, { props: { modelValue: "general" } });
  await flushPromises();
  return wrapper;
}

describe("SettingsNavPanel", () => {
  it("groups the pages under You, Agents, Work and Fleet, and has no Features page", async () => {
    const wrapper = await mountNav(false);
    const groups = wrapper.findAll("[data-settings-nav-group]").map((group) => [
      group.attributes("data-settings-nav-group"),
      group.findAll(".settings-nav__item").map((b) => b.text()),
    ]);
    expect(groups).toEqual([
      ["you", ["General", "Appearance"]],
      ["agents", ["Harnesses", "Permissions", "Skills & tools", "Memory"]],
      ["work", ["Folders", "Machines"]],
      ["fleet", ["Labs", "Updates & about"]],
      ["plugins", ["Plugins0"]],
    ]);
    expect(wrapper.text()).not.toContain("Features");
  });

  it("has no Mods entry of its own: Mods is a tab of Skills & tools", async () => {
    expect((await mountNav(true)).text()).not.toContain("Mods");
  });

  const demo = {
    descriptor: { id: "demo", displayName: "Demo", trustLevel: "built-in", hasFrontend: true, hasBackend: false },
    contributions: { configPage: { title: "Demo integration", navLabel: "Demo", component: { template: "<div />" } } },
  } as const;

  it("has a Plugins entry, no Integrations group, and a child per plugin that is on, from the registry", async () => {
    const wrapper = await mountNav(false);
    expect(wrapper.find("[data-settings-nav-group='integrations']").exists()).toBe(false);
    const group = () => wrapper.get("[data-settings-nav-group='plugins']");
    expect(group().findAll(".settings-nav__item").map((b) => b.text())).toEqual(["Plugins0"]);

    registerPlugin(demo);
    await flushPromises();
    expect(group().get("[data-testid='settings-nav-count']").text()).toBe("1");
    await wrapper.setProps({ modelValue: "plugins" });
    const child = group().findAll("button").find((b) => b.text() === "Demo")!;
    await child.trigger("click");
    expect(wrapper.emitted("update:modelValue")![0]).toEqual(["plugin:demo"]);
    clearPlugins();
  });

  it("drops a plugin's child when it is switched off, and brings it back when it is on", async () => {
    registerPlugin(demo);
    const wrapper = await mountNav(false);
    await wrapper.setProps({ modelValue: "plugin:demo" });
    expect(wrapper.text()).toContain("Demo");
    setPluginEnabled("demo", false);
    await flushPromises();
    expect(wrapper.findAll("button").some((b) => b.text() === "Demo")).toBe(false);
    setPluginEnabled("demo", true);
    await flushPromises();
    expect(wrapper.findAll("button").some((b) => b.text() === "Demo")).toBe(true);
    clearPlugins();
  });

  it("folds the Plugins entry with its chevron", async () => {
    registerPlugin(demo);
    const wrapper = await mountNav(false);
    await wrapper.setProps({ modelValue: "plugins" });
    await wrapper.get("[aria-label='Fold Plugins']").trigger("click");
    expect(wrapper.findAll("button").some((b) => b.text() === "Demo")).toBe(false);
    clearPlugins();
  });

  it("puts the update dot on Updates & about", async () => {
    update.available.value = true;
    const wrapper = await mountNav(false);
    const entry = wrapper.findAll("button").find((b) => b.text().startsWith("Updates"))!;
    expect(entry.find("[aria-label='Update available']").exists()).toBe(true);
    expect(wrapper.findAll("[aria-label='Update available']")).toHaveLength(1);
    await entry.trigger("click");
    expect(wrapper.emitted("update:modelValue")![0]).toEqual(["updates"]);
    update.available.value = false;
  });

  it("puts the number of machines that can update on Updates & about, beside the dot", async () => {
    updates.waiting.value = 0;
    expect((await mountNav(false)).find("[data-testid='updates-badge']").exists()).toBe(false);

    updates.waiting.value = 3;
    update.available.value = true;
    const wrapper = await mountNav(false);
    const entry = wrapper.findAll("button").find((b) => b.text().startsWith("Updates"))!;
    expect(entry.get("[data-testid='updates-badge']").text()).toBe("3");
    // Fleet's own update is still shown while machines can update.
    expect(entry.find("[aria-label='Update available']").exists()).toBe(true);
    expect(wrapper.findAll("[data-testid='updates-badge']")).toHaveLength(1);
    await entry.trigger("click");
    expect(wrapper.emitted("update:modelValue")![0]).toEqual(["updates"]);
    updates.waiting.value = 0;
    update.available.value = false;
  });
});
