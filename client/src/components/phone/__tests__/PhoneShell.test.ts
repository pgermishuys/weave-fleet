import { mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { describe, expect, it, vi } from "vitest";

vi.mock("@/lib/api-client", () => ({ apiFetchOn: vi.fn(), apiFetch: vi.fn() }));
vi.mock("@/composables/phone/use-phone-env", () => ({ usePhoneEnv: () => {} }));
vi.mock("@/components/phone/PhoneToastHost.vue", () => ({ default: { template: "<div />" } }));

import PhoneShell from "@/components/phone/PhoneShell.vue";
import { useModsStore } from "@/stores/mods";

function mountShell(safeMode: boolean) {
  const pinia = createPinia();
  setActivePinia(pinia);
  useModsStore().modsSwitch = { on: true, safeMode };
  return mount(PhoneShell, { slots: { default: "<p>page</p>" }, global: { plugins: [pinia] } });
}

describe("PhoneShell", () => {
  it("puts the safe-mode banner in flow before the stage, not over it", () => {
    const wrapper = mountShell(true);

    const children = Array.from(wrapper.get("[data-testid=phone-shell]").element.children);
    const banner = children.findIndex((child) => child.querySelector("[data-testid=mods-safe-mode-banner]"));
    const stage = children.findIndex((child) => child.classList.contains("ph-stage"));
    expect(banner).toBeGreaterThanOrEqual(0);
    expect(banner).toBeLessThan(stage);
    expect(wrapper.get("[data-testid=mods-safe-mode-turn-on]").classes()).toContain("ph-btn");
  });

  it("draws no banner when mods run", () => {
    expect(mountShell(false).find("[data-testid=mods-safe-mode-banner]").exists()).toBe(false);
  });
});
