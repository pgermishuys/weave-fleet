import { flushPromises, mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("@/lib/api-client", () => ({ apiFetchOn: vi.fn(), apiFetch: vi.fn() }));

import ModsSafeModeBanner from "@/components/mods/ModsSafeModeBanner.vue";
import { useModsStore } from "@/stores/mods";

function mountBanner(state: { on: boolean; safeMode: boolean } | null) {
  const pinia = createPinia();
  setActivePinia(pinia);
  const store = useModsStore();
  store.modsSwitch = state;
  const loadSwitch = vi.spyOn(store, "loadSwitch").mockResolvedValue();
  const setSafeMode = vi.spyOn(store, "setSafeMode").mockResolvedValue({} as never);
  const wrapper = mount(ModsSafeModeBanner, { global: { plugins: [pinia] } });
  return { wrapper, store, loadSwitch, setSafeMode };
}

describe("ModsSafeModeBanner", () => {
  beforeEach(() => vi.clearAllMocks());

  it("says mods are stopped, and why, while safe mode is on", () => {
    const { wrapper } = mountBanner({ on: true, safeMode: true });

    const banner = wrapper.get("[data-testid=mods-safe-mode-banner]");
    expect(banner.attributes("role")).toBe("status");
    expect(banner.text()).toContain("Mods are stopped for now");
    expect(banner.text()).toContain("Fleet started without mods. They start again when Fleet restarts or you turn them back on.");
    expect(banner.get("button").text()).toBe("Turn mods back on");
  });

  it("shows nothing when safe mode is off", () => {
    const { wrapper } = mountBanner({ on: true, safeMode: false });

    expect(wrapper.find("[data-testid=mods-safe-mode-banner]").exists()).toBe(false);
  });

  it("shows nothing when the Mods switch is off, even if the server says safe mode", () => {
    const { wrapper } = mountBanner({ on: false, safeMode: true });

    expect(wrapper.find("[data-testid=mods-safe-mode-banner]").exists()).toBe(false);
  });

  it("reads the switch when it hasn't been read, and not otherwise", () => {
    expect(mountBanner(null).loadSwitch).toHaveBeenCalledTimes(1);
    expect(mountBanner({ on: true, safeMode: true }).loadSwitch).not.toHaveBeenCalled();
  });

  it("turns mods back on from the button, disabled while it waits", async () => {
    const { wrapper, setSafeMode } = mountBanner({ on: true, safeMode: true });
    let finish: () => void = () => {};
    setSafeMode.mockReturnValue(new Promise((resolve) => { finish = () => resolve({} as never); }));

    await wrapper.get("[data-testid=mods-safe-mode-turn-on]").trigger("click");

    expect(setSafeMode).toHaveBeenCalledWith(false);
    expect(wrapper.get("[data-testid=mods-safe-mode-turn-on]").attributes("disabled")).toBeDefined();
    finish();
    await flushPromises();
    expect(wrapper.get("[data-testid=mods-safe-mode-turn-on]").attributes("disabled")).toBeUndefined();
  });

  it("shows the server's refusal inline and keeps the button", async () => {
    const { wrapper, setSafeMode } = mountBanner({ on: true, safeMode: true });
    setSafeMode.mockRejectedValue(new Error("Mods couldn't start."));

    await wrapper.get("[data-testid=mods-safe-mode-turn-on]").trigger("click");
    await flushPromises();

    expect(wrapper.get("[data-testid=mods-safe-mode-error]").text()).toBe("Mods couldn't start.");
    expect(wrapper.get("[data-testid=mods-safe-mode-turn-on]").attributes("disabled")).toBeUndefined();
  });
});
