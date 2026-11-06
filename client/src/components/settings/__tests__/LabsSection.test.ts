import { flushPromises, mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import LabsSection from "@/components/settings/LabsSection.vue";
import { useSettingsNav } from "@/composables/use-settings-nav";
import { saveMachines } from "@/lib/machines";

const { getMock, putMock } = vi.hoisted(() => ({ getMock: vi.fn(), putMock: vi.fn() }));

vi.mock("@/api/client", () => ({ api: { GET: getMock, PUT: putMock } }));
vi.mock("@tanstack/vue-router", () => ({ useRouter: () => ({ navigate: vi.fn() }) }));

async function mountSection() {
  getMock.mockResolvedValue({ data: {} });
  putMock.mockResolvedValue({});
  const wrapper = mount(LabsSection);
  await flushPromises();
  return wrapper;
}

describe("LabsSection", () => {
  beforeEach(() => {
    localStorage.clear();
    setActivePinia(createPinia());
    getMock.mockReset();
    putMock.mockReset();
    // An older home with no machine list: this browser's own list is the one shown.
    vi.stubGlobal("fetch", vi.fn(async () => new Response("{}", { status: 404 })));
  });

  afterEach(() => vi.unstubAllGlobals());

  it("doesn't offer to keep machines live with only this one", async () => {
    const wrapper = await mountSection();

    expect(wrapper.find("[data-testid='live-machines-switch']").exists()).toBe(false);
  });

  it("offers it, off, once there's another machine, and saves the choice", async () => {
    saveMachines([{ id: "m1", name: "mini", baseUrl: "http://mini.example.test:2113", token: "mini-token", addedAt: "2026-10-08T00:00:00.000Z" }]);
    const wrapper = await mountSection();
    const toggle = wrapper.get("[data-testid='live-machines-switch']");
    expect(toggle.attributes("aria-checked")).toBe("false");

    await toggle.trigger("click");
    await flushPromises();

    expect(putMock).toHaveBeenCalledWith("/api/preferences/{key}", { params: { path: { key: "LiveMachines" } }, body: { value: "true" } });
    expect(toggle.attributes("aria-checked")).toBe("true");
  });

  it("has no GitHub Copilot switch: Copilot is a harness like the others", async () => {
    const wrapper = await mountSection();

    expect(wrapper.find("[data-testid='copilot-switch']").exists()).toBe(false);
    expect(wrapper.find("[data-setting-row='copilot']").exists()).toBe(false);
  });

  it("keeps Updates in Machines off until it's turned on, and saves the choice", async () => {
    const wrapper = await mountSection();
    const toggle = wrapper.get("[data-testid='fleet-updates-switch']");
    expect(toggle.attributes("aria-checked")).toBe("false");
    expect(toggle.attributes("aria-label")).toBe("Show updates in Machines");
    expect(wrapper.text()).toContain("Updates in Machines");

    await toggle.trigger("click");
    await flushPromises();

    expect(putMock).toHaveBeenCalledWith("/api/preferences/{key}", { params: { path: { key: "FleetUpdates" } }, body: { value: "true" } });
    expect(toggle.attributes("aria-checked")).toBe("true");
  });

  it("has no Mods switch: Mods is a plugin, switched in Settings › Plugins", async () => {
    const wrapper = await mountSection();

    expect(wrapper.find("[data-testid='mods-switch']").exists()).toBe(false);
    expect(wrapper.find("[data-setting-row='mods']").exists()).toBe(false);
  });
});

describe("LabsSection: what a switch adds", () => {
  beforeEach(() => {
    localStorage.clear();
    setActivePinia(createPinia());
    getMock.mockReset();
    putMock.mockReset();
    vi.stubGlobal("fetch", vi.fn(async () => new Response("{}", { status: 404 })));
  });

  afterEach(() => vi.unstubAllGlobals());

  it("shows what Dreaming adds, with Open, only while it is on", async () => {
    getMock.mockResolvedValue({ data: {} });
    putMock.mockResolvedValue({});
    const wrapper = mount(LabsSection);
    await flushPromises();
    expect(wrapper.find("[data-testid='setting-adds-dreaming']").exists()).toBe(false);

    await wrapper.get("[data-testid='dreaming-switch']").trigger("click");
    await flushPromises();

    expect(wrapper.get("[data-testid='setting-adds-dreaming']").text()).toContain("Adds: Nightly run in Memory");
    await wrapper.get("[data-testid='setting-open-dreaming']").trigger("click");
    expect(useSettingsNav().activeSection.value).toBe("memory");
  });

  it("holds the Workflows switch, which saves the Workflows preference", async () => {
    getMock.mockResolvedValue({ data: {} });
    putMock.mockResolvedValue({});
    const wrapper = mount(LabsSection);
    await flushPromises();
    const toggle = wrapper.get("[data-testid='workflows-switch']");
    expect(toggle.attributes("aria-label")).toBe("Enable workflows");
    await toggle.trigger("click");
    await flushPromises();
    expect(putMock).toHaveBeenCalledWith("/api/preferences/{key}", { params: { path: { key: "Workflows" } }, body: { value: "true" } });
  });
});
