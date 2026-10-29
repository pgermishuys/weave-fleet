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

const { registerPlugin, clearPlugins } = await import("@/plugins/registry");
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
      group.findAll("button").map((b) => b.text()),
    ]);
    expect(groups).toEqual([
      ["you", ["General", "Appearance"]],
      ["agents", ["Harnesses", "Permissions", "Skills & tools", "Memory"]],
      ["work", ["Folders", "Machines"]],
      ["fleet", ["Labs", "Updates & about"]],
    ]);
    expect(wrapper.text()).not.toContain("Features");
  });

  it("has no Mods entry of its own: Mods is a tab of Skills & tools", async () => {
    expect((await mountNav(true)).text()).not.toContain("Mods");
  });

  it("adds an Integrations group with one entry per add-on page, through the contribution point", async () => {
    const wrapper = await mountNav(false);
    expect(wrapper.find("[data-settings-nav-group='integrations']").exists()).toBe(false);
    registerPlugin({
      descriptor: { id: "demo", displayName: "Demo", trustLevel: "built-in", hasFrontend: true, hasBackend: false },
      contributions: { configPage: { title: "Demo integration", navLabel: "Demo", component: { template: "<div />" } } },
    });
    await flushPromises();
    const entry = wrapper.get("[data-settings-nav-group='integrations']").get("button");
    expect(entry.text()).toBe("Demo");
    await entry.trigger("click");
    expect(wrapper.emitted("update:modelValue")![0]).toEqual(["integration:demo"]);
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
