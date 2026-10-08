import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { defineComponent, h, ref, shallowRef } from "vue";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import type { ScannedRepository } from "@/api/client";
import type { MachineConnection } from "@/lib/machines";
import type { Automation, AutomationRun } from "@/stores/automations";

const { apiFetchMock, navigate } = vi.hoisted(() => ({ apiFetchMock: vi.fn(), navigate: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api-client")>()),
  apiFetch: apiFetchMock,
}));
vi.mock("@tanstack/vue-router", () => ({ useNavigate: () => navigate, useRouter: () => ({ navigate }) }));
vi.mock("@/composables/use-signalr-socket", () => ({
  onGlobalEvent: () => () => {},
  onDisconnect: () => () => {},
  onReconnect: () => () => {},
}));
vi.mock("@/lib/automations", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/automations")>()),
  browserTimeZone: () => "Africa/Johannesburg",
}));
vi.mock("@/composables/use-workflows-feature", async () => {
  const { computed } = await import("vue");
  return { useWorkflowsFeature: () => ({ isWorkflowsEnabled: computed(() => false), setWorkflowsEnabled: vi.fn() }) };
});
// The pickers ask the machine the composer is given: these say which one they were given.
const asked: string[] = [];
vi.mock("@/composables/use-enabled-harnesses", async () => {
  const { useMachineTarget } = await import("@/lib/machine-target");
  return {
    useEnabledHarnesses: () => {
      asked.push(`harnesses:${useMachineTarget().key}`);
      const openCode = { type: "opencode", displayName: "OpenCode", capabilities: { supportsWorkflowSteps: true } };
      return { harnesses: ref([openCode]), enabledHarnesses: ref([openCode]), defaultHarnessType: ref("opencode") };
    },
  };
});
vi.mock("@/composables/use-harness-catalog", async () => {
  const { useMachineTarget } = await import("@/lib/machine-target");
  return {
    useHarnessCatalog: () => {
      asked.push(`catalog:${useMachineTarget().key}`);
      return {
        catalog: ref(null), agents: ref([]), models: ref([]), isSupported: ref(false), isCurrent: ref(false),
        isLoading: ref(false), error: ref(null),
      };
    },
  };
});
const homeRepositories = ref<ScannedRepository[]>([]);
const atlasRepositories = ref<ScannedRepository[]>([]);
vi.mock("@/composables/use-repositories", async () => {
  const { useMachineTarget } = await import("@/lib/machine-target");
  return {
    useRepositories: () => ({
      repositories: useMachineTarget().key === "m-atlas" ? atlasRepositories : homeRepositories,
      isLoading: shallowRef(false),
      error: shallowRef(null),
      scannedAt: shallowRef(1),
      refresh: vi.fn(),
    }),
  };
});
vi.mock("@/composables/use-repository-detail", () => ({
  useRepositoryDetail: () => ({ detail: shallowRef(null), isLoading: shallowRef(false), error: shallowRef(null) }),
}));

import AutomationDetailPanel from "../AutomationDetailPanel.vue";
import AutomationsNavPanel from "../AutomationsNavPanel.vue";
import { useAutomationsNav } from "@/composables/use-automations-nav";
import { NEW_SESSION_DEFAULTS_KEY, newSessionDefaultsKey } from "@/composables/use-new-session-defaults";
import { useMachinesStore } from "@/stores/machines";

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

const pocketNotes: ScannedRepository = { name: "pocket-notes", path: "/home/me/source/pocket-notes", parentRoot: "/home/me/source" };
const harborApi: ScannedRepository = { name: "harbor-api", path: "/srv/code/harbor-api", parentRoot: "/srv/code" };
const atlas: MachineConnection = { id: "m-atlas", name: "atlas", baseUrl: "http://atlas.test:2113", token: "fmt_atlas", os: "linux", addedAt: "2026-10-01T00:00:00Z" };

const nightly: Automation = {
  id: "a-night",
  name: "Nightly check",
  prompt: "Check the build",
  triggerType: "schedule",
  triggerConfig: "0 2 * * *",
  maxConcurrentRuns: 1,
  maxRunsPerHour: 10,
  timeoutMinutes: 30,
  isEnabled: true,
  workspaceId: harborApi.path,
  model: null,
  agent: null,
  createdAt: "2026-10-01T09:00:00Z",
  updatedAt: null,
  targetType: "new_session",
  timeZone: "Africa/Johannesburg",
  isolation: "worktree",
  baseBranch: null,
  harnessType: null,
  nextRunAt: null,
  lastRun: null,
  targetMachineId: "m-atlas",
};

const onAtlas: AutomationRun = {
  id: "r1", automationId: "a-night", trigger: "manual", scheduledFor: null, startedAt: "2026-10-08T07:00:00.0000000Z",
  state: "done", sessionId: "atlas-session", instanceId: "atlas-inst", error: null, machineId: "m-atlas", machineName: "atlas",
};

let list: Automation[] = [];
let runs: AutomationRun[] = [];
const sent: { method: string; url: string; body: Record<string, unknown> }[] = [];

function serve(url: string, init?: RequestInit): Response {
  const method = init?.method ?? "GET";
  const body = init?.body ? (JSON.parse(String(init.body)) as Record<string, unknown>) : {};
  if (method !== "GET") sent.push({ method, url, body });
  if (method === "GET" && url === "/api/automations") return json({ automations: list });
  if (method === "GET" && /\/api\/automations\/[^/]+\/runs$/.test(url)) return json({ runs });
  if (method === "POST" && url === "/api/automations") {
    const automation = { ...nightly, ...body, id: `a${list.length + 1}`, isEnabled: true, lastRun: null } as Automation;
    list = [...list, automation];
    return json(automation, 201);
  }
  return json({ error: `not faked: ${method} ${url}` }, 404);
}

const Screen = defineComponent({
  setup() {
    const nav = useAutomationsNav();
    return () =>
      h("div", [
        h(AutomationsNavPanel, {
          modelValue: nav.activeAutomationId.value,
          "onUpdate:modelValue": nav.setActiveAutomation,
          onCreate: nav.startCreate,
        }),
        h(AutomationDetailPanel),
      ]);
  },
});

describe("An automation that runs on another machine", () => {
  let wrapper: VueWrapper;

  async function mountScreen(): Promise<void> {
    wrapper = mount(Screen, { attachTo: document.body, global: { stubs: { teleport: false } } });
    await flushPromises();
  }

  async function startNew(text: string): Promise<void> {
    await wrapper.find("[data-testid='new-automation']").trigger("click");
    await flushPromises();
    await wrapper.find("[data-testid='automation-message']").setValue(text);
    await flushPromises();
  }

  async function pickMachine(key: string): Promise<void> {
    await wrapper.find("[data-testid='new-session-machine']").trigger("click");
    await flushPromises();
    document.querySelector<HTMLElement>(`[data-testid='new-session-machine-${key}']`)!.click();
    await flushPromises();
  }

  beforeEach(() => {
    vi.useFakeTimers({ toFake: ["Date"] });
    vi.setSystemTime(new Date(2026, 9, 8, 10, 0, 0));
    setActivePinia(createPinia());
    // Home answers as hangar and keeps no list of its own here, so the browser's list is the one; atlas has no sessions.
    vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input instanceof Request ? input.url : input);
      if (url.endsWith("/api/machine")) return Response.json({ id: "home-id", name: "hangar", os: "linux" });
      if (url.endsWith("/api/machines")) return new Response(null, { status: 404 });
      return Response.json([]);
    }));
    localStorage.setItem(NEW_SESSION_DEFAULTS_KEY, JSON.stringify({ lastFolder: { kind: "repository", path: pocketNotes.path } }));
    localStorage.setItem(newSessionDefaultsKey("m-atlas"), JSON.stringify({ lastFolder: { kind: "repository", path: harborApi.path } }));
    homeRepositories.value = [pocketNotes];
    atlasRepositories.value = [harborApi];
    list = [];
    runs = [];
    sent.length = 0;
    asked.length = 0;
    navigate.mockReset();
    apiFetchMock.mockReset();
    apiFetchMock.mockImplementation(async (url: string, init?: RequestInit) => serve(url, init));
    const nav = useAutomationsNav();
    nav.clearSelection();
    nav.resetDraft();
  });

  afterEach(() => {
    wrapper?.unmount();
    document.body.innerHTML = "";
    localStorage.clear();
    vi.unstubAllGlobals();
    vi.useRealTimers();
  });

  it("has no Machine chip while this is the only machine", async () => {
    await mountScreen();
    await startNew("Every night at 2, check the build");

    expect(wrapper.find("[data-testid='new-session-machine']").exists()).toBe(false);
    expect(wrapper.find("[data-testid='automation-plan-machine']").exists()).toBe(false);
  });

  it("runs on the machine picked: its folders, harnesses and models, and the machine is sent", async () => {
    localStorage.setItem("weave:machines", JSON.stringify([atlas]));
    await mountScreen();
    await startNew("Every night at 2, check the build");

    expect(wrapper.get("[data-testid='new-session-machine']").text()).toContain("hangar");
    expect(wrapper.get("[data-testid='new-session-folder-chip']").text()).toContain("pocket-notes");
    asked.length = 0;

    await pickMachine("m-atlas");

    expect(wrapper.get("[data-testid='new-session-machine']").text()).toContain("atlas");
    expect(wrapper.get("[data-testid='new-session-folder-chip']").text()).toContain("harbor-api");
    expect(asked).toEqual(expect.arrayContaining(["harnesses:m-atlas", "catalog:m-atlas"]));
    expect(asked.some((call) => !call.endsWith(":m-atlas"))).toBe(false);
    expect(wrapper.get("[data-testid='automation-plan-machine']").text()).toBe("On atlas:");
    // What was typed stays.
    expect((wrapper.get("[data-testid='automation-message']").element as HTMLTextAreaElement).value).toBe("Every night at 2, check the build");

    await wrapper.find("[data-testid='automation-submit']").trigger("click");
    await flushPromises();

    expect(sent[0]).toMatchObject({
      method: "POST",
      url: "/api/automations",
      body: { workspaceId: harborApi.path, isolation: "worktree", targetMachineId: "m-atlas" },
    });
  });

  it("goes back to this machine with its own folder", async () => {
    localStorage.setItem("weave:machines", JSON.stringify([atlas]));
    await mountScreen();
    await startNew("Every night at 2, check the build");
    await pickMachine("m-atlas");

    await pickMachine("home");

    expect(wrapper.get("[data-testid='new-session-folder-chip']").text()).toContain("pocket-notes");
    expect(wrapper.find("[data-testid='automation-plan-machine']").exists()).toBe(false);
    await wrapper.find("[data-testid='automation-submit']").trigger("click");
    await flushPromises();
    expect(sent[0]!.body.targetMachineId).toBeNull();
  });

  it("shows the machine in the list and on its runs, and opens a run's session on that machine", async () => {
    localStorage.setItem("weave:machines", JSON.stringify([atlas]));
    list = [nightly, { ...nightly, id: "a-here", name: "Weekly digest", targetMachineId: null, workspaceId: pocketNotes.path }];
    runs = [onAtlas];
    await mountScreen();
    const openOn = vi.spyOn(useMachinesStore(), "openOn").mockImplementation(() => {});

    const rows = wrapper.findAll("[data-testid='automation-row']");
    expect(rows[0]!.get("[data-testid='automation-row-machine']").text()).toBe("atlas");
    expect(rows[1]!.find("[data-testid='automation-row-machine']").exists()).toBe(false);

    await rows[0]!.trigger("click");
    await flushPromises();

    expect(wrapper.get("[data-testid='new-session-machine']").text()).toContain("atlas");
    expect(wrapper.get("[data-testid='new-session-folder-chip']").text()).toContain("harbor-api");
    expect(wrapper.find("[data-testid='automation-submit']").exists()).toBe(false);
    const run = wrapper.get("[data-testid='automation-run']");
    expect(run.get("[data-testid='automation-run-machine']").text()).toBe("on atlas");

    await run.trigger("click");

    expect(openOn).toHaveBeenCalledWith("m-atlas", "/sessions/atlas-session?instanceId=atlas-inst");
    expect(navigate).not.toHaveBeenCalled();
  });
});
