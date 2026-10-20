import { enableAutoUnmount, flushPromises, mount } from "@vue/test-utils";
import { afterEach, describe, expect, it, vi } from "vitest";
import { reactive, ref } from "vue";

const store = vi.hoisted(() => ({ state: null as unknown as { isSwitchedOn: boolean; loadSwitch: ReturnType<typeof vi.fn> } }));
vi.mock("@/stores/mods", () => ({ useModsStore: () => store.state }));

const updates = vi.hoisted(() => ({ enabled: { value: false }, waiting: { value: 0 } }));
vi.mock("@/composables/use-fleet-updates", () => ({
  useFleetUpdatesWatch: () => ({ enabled: ref(updates.enabled.value), waiting: ref(updates.waiting.value) }),
}));

const { default: SettingsNavPanel } = await import("@/components/settings/SettingsNavPanel.vue");

enableAutoUnmount(afterEach);

async function mountNav(on: boolean) {
  store.state = reactive({ isSwitchedOn: on, loadSwitch: vi.fn().mockResolvedValue(undefined) });
  const wrapper = mount(SettingsNavPanel, { props: { modelValue: "workspace" } });
  await flushPromises();
  return wrapper;
}

describe("SettingsNavPanel", () => {
  it("shows Mods only when the Mods switch is on, and selects it", async () => {
    expect((await mountNav(false)).text()).not.toContain("Mods");
    const wrapper = await mountNav(true);
    await wrapper.findAll("button").find((b) => b.text() === "Mods")!.trigger("click");
    expect(wrapper.emitted("update:modelValue")![0]).toEqual(["mods"]);
    expect(store.state.loadSwitch).toHaveBeenCalled();
  });

  it("puts the number of machines that can update on Machines, and no Updates entry of its own", async () => {
    updates.waiting.value = 0;
    const none = await mountNav(false);
    expect(none.find("[data-testid='updates-badge']").exists()).toBe(false);
    expect(none.text()).not.toContain("Updates");

    updates.waiting.value = 3;
    const wrapper = await mountNav(false);
    const entry = wrapper.findAll("button").find((b) => b.text().startsWith("Machines"))!;
    expect(entry.get("[data-testid='updates-badge']").text()).toBe("3");
    expect(wrapper.text()).not.toContain("Updates");
    await entry.trigger("click");
    expect(wrapper.emitted("update:modelValue")![0]).toEqual(["machines"]);
  });
});
