import { flushPromises, mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { reactive } from "vue";
import type { HarnessInfo } from "@/api/client";
import PermissionsSection from "@/components/settings/PermissionsSection.vue";
import { forgetHarnessLists } from "@/composables/use-harnesses";
import { useSettingsNav } from "@/composables/use-settings-nav";
import type { MachineAgentWork, MachineEntry } from "@/stores/machines";

const { getMock, putMock, apiFetchMock, machinesStore } = vi.hoisted(() => ({
  getMock: vi.fn(),
  putMock: vi.fn(),
  apiFetchMock: vi.fn(),
  machinesStore: { current: null as unknown },
}));

vi.mock("@/api/client", () => ({ api: { GET: getMock, PUT: putMock } }));
vi.mock("@/lib/api-client", () => ({ apiFetch: apiFetchMock }));
vi.mock("@/stores/machines", async (original) => ({
  ...(await original<typeof import("@/stores/machines")>()),
  useMachinesStore: () => machinesStore.current,
}));

function harness(type: string, displayName: string, levels: boolean): HarnessInfo {
  return {
    type,
    displayName,
    available: true,
    userEnabled: true,
    state: "ready",
    capabilities: { supportsPermissionLevels: levels, supportsAgentBrowser: levels } as HarnessInfo["capabilities"],
    presentation: { order: 0, eyebrow: "CLI harness", description: "", icon: "terminal", permissionModes: null },
  };
}

/** Pi can't take a level, nor the browser. */
const HARNESSES: HarnessInfo[] = [harness("opencode2", "OpenCode 2", true), harness("claude-code", "Claude Code", true), harness("pi", "Pi", false)];

function machine(key: string, name: string, level: string): MachineEntry {
  return {
    key, name, os: "linux", baseUrl: `http://${name}:6262`, isHome: false, isLive: false, connection: null,
    capabilities: { harnesses: null, sessions: { working: 0, needsYou: 0 }, permissionLevel: level },
  };
}

function useMachines(work: Record<string, MachineAgentWork>, entries: MachineEntry[]) {
  const store = reactive({
    agentWork: { ...work },
    agentWorkEntries: entries,
    setAgentWork: vi.fn(async (key: string, value: MachineAgentWork) => {
      store.agentWork = { ...store.agentWork, [key]: value };
    }),
    loadHome: vi.fn(),
    syncFromServer: vi.fn(),
    startPolling: () => () => {},
  });
  machinesStore.current = store;
  return store;
}

async function mountSection(preferences: Record<string, string> = {}, browserOn = true) {
  getMock.mockImplementation((path: string) =>
    Promise.resolve({ data: path === "/api/harnesses" ? HARNESSES : preferences, response: new Response(null, { status: 200 }) }));
  apiFetchMock.mockResolvedValue(new Response(JSON.stringify({ enabled: browserOn, pages: "session", scripts: false }), { status: 200 }));
  putMock.mockResolvedValue({});
  const wrapper = mount(PermissionsSection);
  await flushPromises();
  return wrapper;
}

function saved(key: string, value: string) {
  return ["/api/preferences/{key}", { params: { path: { key } }, body: { value } }];
}

const checked = (wrapper: Awaited<ReturnType<typeof mountSection>>, testid: string) =>
  wrapper.get(`[data-testid='${testid}']`).attributes("aria-checked");

describe("PermissionsSection (What agents can do)", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    forgetHarnessLists();
    getMock.mockReset();
    putMock.mockReset();
    useMachines({}, []);
  });

  it("keeps today's defaults: edits and commands allowed, messages off, unattended runs allowed, each marked default", async () => {
    const wrapper = await mountSection();

    expect(checked(wrapper, "reach-edits-allowed")).toBe("true");
    expect(checked(wrapper, "reach-commands-allowed")).toBe("true");
    expect(checked(wrapper, "reach-messages-off")).toBe("true");
    expect(checked(wrapper, "reach-unattended-all")).toBe("true");
    for (const row of ["edits", "commands", "messages", "unattended", "machines"])
      expect(wrapper.get(`[data-testid='reach-row-${row}'] .reach-tag`).text()).toBe("default");
  });

  it("lists the rows in order: edits, commands, messages, other machines, nobody watching", async () => {
    const wrapper = await mountSection();

    expect(wrapper.findAll("[data-testid^='reach-row-']").map((row) => row.attributes("data-testid")))
      .toEqual(["reach-row-edits", "reach-row-commands", "reach-row-messages", "reach-row-machines", "reach-row-unattended"]);
  });

  it("saves Edit files Ask as the ask level, which makes commands ask too", async () => {
    const wrapper = await mountSection();

    await wrapper.get("[data-testid='reach-edits-ask']").trigger("click");

    expect(putMock).toHaveBeenCalledWith(...saved("PermissionLevel", "ask"));
    expect(checked(wrapper, "reach-commands-ask")).toBe("true");
    expect(wrapper.get("[data-testid='reach-row-edits'] .reach-tag").text()).toBe("default: Allowed");
  });

  it("saves Run commands Ask as the edits level: edits still run", async () => {
    const wrapper = await mountSection();

    await wrapper.get("[data-testid='reach-commands-ask']").trigger("click");

    expect(putMock).toHaveBeenCalledWith(...saved("PermissionLevel", "edits"));
    expect(checked(wrapper, "reach-edits-allowed")).toBe("true");
  });

  it("lists a harness at a level of its own under the rows it differs on, and one Fleet can't set on both", async () => {
    const wrapper = await mountSection({ PermissionLevel: "ask", "PermissionLevel.claude-code": "edits" });

    const edits = wrapper.get("[data-testid='reach-row-edits']");
    const commands = wrapper.get("[data-testid='reach-row-commands']");
    expect(edits.get<HTMLSelectElement>("[data-testid='reach-exception-edits-claude-code'] select").element.value).toBe("allowed");
    expect(commands.find("[data-testid='reach-exception-commands-claude-code']").exists()).toBe(false);
    expect(edits.get("[data-testid='reach-unleveled-edits-pi']").text()).toContain("Fleet can't set it");
    expect(commands.find("[data-testid='reach-unleveled-commands-pi']").exists()).toBe(true);
  });

  it("adds an exception from + exception, and a harness set back to the default's value follows the default again", async () => {
    const wrapper = await mountSection();

    await wrapper.get("[data-testid='reach-add-commands']").setValue("claude-code");
    expect(putMock).toHaveBeenLastCalledWith(...saved("PermissionLevel.claude-code", "edits"));

    await wrapper.get("[data-testid='reach-exception-commands-claude-code'] select").setValue("allowed");
    expect(putMock).toHaveBeenLastCalledWith(...saved("PermissionLevel.claude-code", ""));
  });

  it("says whether agents get the browser on Run commands, and opens the Browser group from it", async () => {
    const wrapper = await mountSection();

    const chip = wrapper.get("[data-testid='reach-browser-chip']");
    expect(chip.text()).toBe("Browser On, not in Pi");
    await chip.trigger("click");
    expect(useSettingsNav().activeAnchor.value).toBe("browser");
  });

  it("shows the browser as off when agents can't use it", async () => {
    const wrapper = await mountSection({}, false);

    expect(wrapper.get("[data-testid='reach-browser-chip']").text()).toBe("Browser Off");
  });

  it("turns messages between sessions on with the same preference Labs had, for sessions started afterwards", async () => {
    const wrapper = await mountSection();

    expect(wrapper.get("[data-testid='reach-row-messages']").text()).toContain("Applies to sessions started afterwards.");
    await wrapper.get("[data-testid='reach-messages-allowed']").trigger("click");

    expect(putMock).toHaveBeenCalledWith(...saved("SessionMessages", "true"));
  });

  it("says nowhere else that a change waits for new sessions", async () => {
    const wrapper = await mountSection();

    expect(wrapper.text().match(/sessions started afterwards/g)).toHaveLength(1);
  });

  it("lists machines set to Always or Never as exceptions, with the level work runs at there, and changes them", async () => {
    const store = useMachines({ m1: "always", m2: "ask", m3: "never" }, [
      machine("m1", "taron", "edits"), machine("m2", "vega", "all"), machine("m3", "orion", "ask"),
    ]);
    const wrapper = await mountSection();
    const row = wrapper.get("[data-testid='reach-row-machines']");

    expect(row.get("[data-testid='reach-machine-m1']").text()).toContain("runs at Allow edits");
    expect(row.find("[data-testid='reach-machine-m2']").exists()).toBe(false);
    expect(row.find("[data-testid='reach-machine-m3']").exists()).toBe(true);

    await row.get("[data-testid='reach-machine-m3'] select").setValue("ask");
    expect(store.setAgentWork).toHaveBeenLastCalledWith("m3", "ask");

    await wrapper.get("[data-testid='reach-add-machines']").setValue("m2|never");
    expect(store.setAgentWork).toHaveBeenLastCalledWith("m2", "never");
  });

  it("points to Machines when there's no other machine", async () => {
    const wrapper = await mountSection();

    expect(wrapper.get("[data-testid='reach-row-machines']").text()).toContain("No other machines yet.");
  });

  it("keeps unattended runs allowed until told otherwise", async () => {
    const wrapper = await mountSection();

    await wrapper.get("[data-testid='reach-unattended-deny']").trigger("click");

    expect(putMock).toHaveBeenLastCalledWith(...saved("PermissionUnattended", "deny"));
    expect(wrapper.text()).toContain("Anything that would ask is refused");
  });
});
