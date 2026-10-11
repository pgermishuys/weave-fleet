import { enableAutoUnmount, flushPromises, mount } from "@vue/test-utils";
import { afterEach, describe, expect, it, vi } from "vitest";
import { reactive } from "vue";

const store = vi.hoisted(() => ({ state: null as unknown as { isSwitchedOn: boolean; loadSwitch: ReturnType<typeof vi.fn> } }));
vi.mock("@/stores/mods", () => ({ useModsStore: () => store.state }));

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
});
