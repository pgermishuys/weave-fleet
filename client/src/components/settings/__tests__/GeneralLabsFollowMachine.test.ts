import { flushPromises, mount, type DOMWrapper } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { MACHINE_TARGET, type MachineTarget } from "@/lib/machine-target";
import { saveMachines } from "@/lib/machines";

// The live machine (this app's preferences) answers through the global api; the picked machine through its own.
const live = vi.hoisted(() => ({ GET: vi.fn(), PUT: vi.fn(), POST: vi.fn(), DELETE: vi.fn() }));
vi.mock("@/api/client", async (importOriginal) => ({ ...(await importOriginal<object>()), api: live }));
vi.mock("@tanstack/vue-router", () => ({ useRouter: () => ({ navigate: vi.fn() }) }));
// Settings shows kestrel, picked in its menu.
const picked = vi.hoisted(() => ({ other: true }));
vi.mock("@/composables/use-settings-machine", async () => {
  const { computed: c } = await import("vue");
  return {
    useSettingsMachine: () => ({ enabled: c(() => true), isOther: c(() => picked.other), entry: c(() => ({ name: "kestrel" })) }),
  };
});

const { default: GeneralSection } = await import("@/components/settings/GeneralSection.vue");
const { default: LabsSection } = await import("@/components/settings/LabsSection.vue");

// A machine's store is defined once per key, with the api it was first asked through: each test gets its own key.
let made = 0;

function kestrel(preferences: Record<string, string> | null = {}) {
  const api = {
    GET: vi.fn().mockResolvedValue(preferences ? { data: preferences } : { error: { status: 502 } }),
    PUT: vi.fn().mockResolvedValue({}),
    POST: vi.fn(),
    DELETE: vi.fn(),
  };
  const target = { key: `kestrel-${++made}`, connection: { id: "kestrel" }, isLive: false, api } as unknown as MachineTarget;
  return { api, target };
}

async function mountOn(component: object, target: MachineTarget) {
  const wrapper = mount(component, { global: { provide: { [MACHINE_TARGET as symbol]: () => target } } });
  await flushPromises();
  return wrapper;
}

const saved = (key: string, value: string) => ["/api/preferences/{key}", { params: { path: { key } }, body: { value } }];

/** The ids of the rows marked as this app's, not the machine's. */
function appRows(wrapper: { findAll: (selector: string) => DOMWrapper<Element>[] }): string[] {
  return wrapper.findAll("[data-setting-row]")
    .filter((row) => row.find("[data-testid='this-app-badge']").exists())
    .map((row) => row.attributes("data-setting-row")!);
}

describe("General and Labs on another machine", () => {
  beforeEach(() => {
    localStorage.clear();
    setActivePinia(createPinia());
    picked.other = true;
    for (const fn of Object.values(live)) fn.mockReset();
    live.GET.mockResolvedValue({ data: { RetryAfterLimits: "true", NewSessionStarters: "true", Dreaming: "false" } });
    live.PUT.mockResolvedValue({});
    vi.stubGlobal("fetch", vi.fn(async () => new Response("{}", { status: 404 })));
  });

  afterEach(() => vi.unstubAllGlobals());

  it("General shows and saves the machine's own settings there, and this app's here", async () => {
    const { api, target } = kestrel({ RetryAfterLimits: "true", SessionRecap: "true" });
    const wrapper = await mountOn(GeneralSection, target);

    expect(wrapper.get("[aria-label='Enable session recap']").attributes("aria-checked")).toBe("true");
    await wrapper.get("[data-testid='retry-after-limits-switch']").trigger("click");
    await wrapper.get("[data-testid='restart-continue-switch']").trigger("click");
    await flushPromises();
    expect(api.PUT.mock.calls).toEqual([saved("RetryAfterLimits", "false"), saved("RestartContinueAuto", "true")]);

    await wrapper.get("[data-testid='starters-switch']").trigger("click");
    await wrapper.get("[data-testid='usage-limits-chip-switch']").trigger("click");
    await wrapper.get("[data-testid='large-pastes-select']").setValue("attach");
    await flushPromises();
    expect(live.PUT.mock.calls).toEqual([saved("NewSessionStarters", "false"), saved("UsageLimitsChip", "false"), saved("LargePastes", "attach")]);
    expect(api.PUT).toHaveBeenCalledTimes(2);

    expect(appRows(wrapper)).toEqual(["starters", "large-pastes", "notifications", "usage-chip", "board"]);
    expect(wrapper.get("[data-testid='this-app-badge']").text()).toBe("This app · not kestrel's");
  });

  it("Labs shows and saves the machine's own experiments there, and this app's here", async () => {
    saveMachines([{ id: "kestrel", name: "kestrel", baseUrl: "http://kestrel.example.test:2113", token: "kestrel-token", addedAt: "2026-11-08T00:00:00.000Z" }]);
    const { api, target } = kestrel({ Memory: "true", Workflows: "true" });
    const wrapper = await mountOn(LabsSection, target);

    // Search older notes needs Memory on: kestrel's Memory, not this app's.
    expect(wrapper.get("[data-testid='memory-search-switch']").attributes("disabled")).toBeUndefined();
    expect(wrapper.get("[data-testid='workflows-switch']").attributes("aria-checked")).toBe("true");
    for (const id of ["dreaming-switch", "memory-search-switch", "page-choice-switch", "workflows-switch"]) {
      await wrapper.get(`[data-testid='${id}']`).trigger("click");
    }
    await flushPromises();
    expect(api.PUT.mock.calls).toEqual([saved("Dreaming", "true"), saved("MemorySearch", "true"), saved("PageChoice", "true"), saved("Workflows", "false")]);

    for (const id of ["live-machines-switch", "fleet-updates-switch", "jobs-switch", "mission-control-switch"]) {
      await wrapper.get(`[data-testid='${id}']`).trigger("click");
    }
    await flushPromises();
    expect(live.PUT.mock.calls.map(([, body]) => (body as { params: { path: { key: string } } }).params.path.key))
      .toEqual(["LiveMachines", "FleetUpdates", "Jobs", "features.mission-control.enabled"]);
    expect(api.PUT).toHaveBeenCalledTimes(4);

    expect(appRows(wrapper)).toEqual(["live-machines", "fleet-updates", "jobs", "mission-control"]);
  });

  it("a machine whose settings can't be read says so and locks only its own rows", async () => {
    const { api, target } = kestrel(null);
    const wrapper = await mountOn(GeneralSection, target);

    expect(wrapper.get("[data-testid='machine-settings-unread']").text()).toContain("Couldn't read kestrel's settings");
    expect(wrapper.get("[data-testid='retry-after-limits-switch']").attributes("disabled")).toBeDefined();
    expect(wrapper.get("[data-testid='starters-switch']").attributes("disabled")).toBeUndefined();

    api.GET.mockResolvedValue({ data: { RetryAfterLimits: "false" } });
    await wrapper.get("[data-testid='machine-settings-retry']").trigger("click");
    await flushPromises();
    expect(wrapper.find("[data-testid='machine-settings-unread']").exists()).toBe(false);
    expect(wrapper.get("[data-testid='retry-after-limits-switch']").attributes("aria-checked")).toBe("false");
  });

  it("on the live machine nothing is marked, and every row is saved there", async () => {
    picked.other = false;
    const wrapper = mount(GeneralSection);
    await flushPromises();

    expect(wrapper.find("[data-testid='this-app-badge']").exists()).toBe(false);
    await wrapper.get("[data-testid='retry-after-limits-switch']").trigger("click");
    await flushPromises();
    expect(live.PUT).toHaveBeenCalledWith(...saved("RetryAfterLimits", "false"));
  });
});

