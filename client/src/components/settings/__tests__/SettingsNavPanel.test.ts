import { enableAutoUnmount, flushPromises, mount } from "@vue/test-utils";
import { afterEach, describe, expect, it, vi } from "vitest";
import { reactive } from "vue";

const store = vi.hoisted(() => ({ state: null as unknown as { isSwitchedOn: boolean; loadSwitch: ReturnType<typeof vi.fn> } }));
vi.mock("@/stores/mods", () => ({ useModsStore: () => store.state }));

const { default: SettingsNavPanel } = await import("@/components/settings/SettingsNavPanel.vue");

enableAutoUnmount(afterEach);

function mountNav(on: boolean) {
  store.state = reactive({ isSwitchedOn: on, loadSwitch: vi.fn().mockResolvedValue(undefined) });
  return mount(SettingsNavPanel, { props: { modelValue: "workspace" } });
}

describe("SettingsNavPanel", () => {
  it("shows Mods only when the Mods switch is on", async () => {
    const off = mountNav(false);
    await flushPromises();
    expect(off.text()).not.toContain("Mods");
    const on = mountNav(true);
    await flushPromises();
    expect(on.text()).toContain("Mods");
    expect(store.state.loadSwitch).toHaveBeenCalled();
  });

  it("selects the Mods section", async () => {
    const wrapper = mountNav(true);
    const item = wrapper.findAll("button").find((b) => b.text() === "Mods")!;
    await item.trigger("click");
    expect(wrapper.emitted("update:modelValue")![0]).toEqual(["mods"]);
  });
});
