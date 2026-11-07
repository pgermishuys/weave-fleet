import { config, mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { beforeEach, describe, expect, it, vi } from "vitest";
import SettingsMachineSwitch from "@/components/settings/SettingsMachineSwitch.vue";
import { resetSettingsMachine, useSettingsMachine } from "@/composables/use-settings-machine";
import { saveMachines, setActiveMachine, type MachineConnection } from "@/lib/machines";
import { useMachinesStore } from "@/stores/machines";

const falcon: MachineConnection = {
  id: "ffffffffffffffffffffffffffffffff",
  name: "falcon",
  baseUrl: "http://falcon.example.test:2113",
  token: "falcon-token-0123456789",
  os: "linux",
  addedAt: "2026-11-07T00:00:00.000Z",
};

function setUp() {
  const pinia = createPinia();
  setActivePinia(pinia);
  config.global.plugins = [pinia];
  setActiveMachine(null);
  saveMachines([falcon]);
  vi.stubGlobal("fetch", vi.fn(async () => Response.json([])));
  resetSettingsMachine();
  return useMachinesStore();
}

describe("the Settings machine switch", () => {
  beforeEach(() => localStorage.clear());

  it("shows the machine you're on, and pages ask it", () => {
    const machines = setUp();
    machines.setCurrent(falcon.id);
    const settings = useSettingsMachine();

    expect(settings.enabled.value).toBe(true);
    expect(settings.key.value).toBe(falcon.id);
    expect(settings.target.value).toMatchObject({ key: falcon.id, isLive: false });
    expect(settings.isOther.value).toBe(true);
  });

  it("follows a pick until Settings opens again, then starts from the machine you're on", () => {
    const machines = setUp();
    machines.setCurrent(falcon.id);
    const settings = useSettingsMachine();

    settings.pick("home");
    expect(settings.key.value).toBe("home");
    expect(settings.isOther.value).toBe(false);

    resetSettingsMachine();
    expect(settings.key.value).toBe(falcon.id);
  });

  it("lists every machine with its sessions and a check on the one shown; picking one changes it", async () => {
    const machines = setUp();
    machines.setCurrent(falcon.id);
    // The popover's menu is drawn open, in place (its own open/close is reka-ui's).
    const open = { template: "<div><slot /></div>" };
    const view = mount(SettingsMachineSwitch, { global: { stubs: { Popover: open, PopoverTrigger: open, PopoverContent: open } } });

    expect(view.get("[data-testid='settings-machine-switch']").text()).toContain("falcon");
    const items = view.findAll(".mx-item");
    expect(items).toHaveLength(2);
    expect(view.text()).toContain("Settings for");
    expect(view.find(`[data-testid='settings-machine-${falcon.id}'] [aria-label='Shown']`).exists()).toBe(true);
    expect(view.find("[data-testid='settings-machine-home'] [aria-label='Shown']").exists()).toBe(false);

    await view.get("[data-testid='settings-machine-home']").trigger("click");
    expect(useSettingsMachine().key.value).toBe("home");
    view.unmount();
  });
});
