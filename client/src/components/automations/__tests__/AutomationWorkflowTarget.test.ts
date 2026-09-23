import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { defineComponent, h, ref, shallowRef } from "vue";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import type { ScannedRepository } from "@/api/client";
import type { Automation, AutomationRun } from "@/stores/automations";
import type { Workflow, WorkflowLibrary } from "@/lib/workflows";

const { apiFetchMock, navigate, workflowsOn } = vi.hoisted(() => ({
  apiFetchMock: vi.fn(),
  navigate: vi.fn(),
  workflowsOn: { value: true },
}));
vi.mock("@/lib/api-client", () => ({ apiFetch: apiFetchMock }));
vi.mock("@tanstack/vue-router", () => ({ useNavigate: () => navigate, useRouter: () => ({ navigate }) }));
vi.mock("@/composables/use-signalr-socket", () => ({ onGlobalEvent: () => () => {} }));
vi.mock("@/lib/automations", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/automations")>()),
  browserTimeZone: () => "Africa/Johannesburg",
}));
vi.mock("@/composables/use-workflows-feature", async () => {
  const { computed, ref: vueRef } = await import("vue");
  const on = vueRef(true);
  return {
    useWorkflowsFeature: () => {
      on.value = workflowsOn.value;
      return { isWorkflowsEnabled: computed(() => on.value), setWorkflowsEnabled: vi.fn() };
    },
  };
});
vi.mock("@/composables/use-enabled-harnesses", () => {
  const openCode = { type: "opencode", displayName: "OpenCode", capabilities: { supportsWorkflowSteps: true } };
  const openCode2 = { type: "opencode2", displayName: "OpenCode 2", capabilities: { supportsWorkflowSteps: true } };
  const claude = { type: "claude-code", displayName: "Claude Code", capabilities: { supportsWorkflowSteps: false } };
  return {
    useEnabledHarnesses: () => ({
      harnesses: ref([openCode, openCode2, claude]),
      enabledHarnesses: ref([openCode, openCode2, claude]),
      defaultHarnessType: ref("opencode"),
    }),
  };
});
vi.mock("@/composables/use-harness-catalog", () => ({
  useHarnessCatalog: () => ({
    catalog: ref(null),
    agents: ref([]),
    models: ref([]),
    isSupported: ref(false),
    isCurrent: ref(false),
    isLoading: ref(false),
    error: ref(null),
  }),
}));
const repositories = ref<ScannedRepository[]>([]);
vi.mock("@/composables/use-repositories", () => ({
  useRepositories: () => ({ repositories, isLoading: shallowRef(false), error: shallowRef(null), scannedAt: shallowRef(1), refresh: vi.fn() }),
}));
vi.mock("@/composables/use-repository-detail", () => ({
  useRepositoryDetail: () => ({ detail: shallowRef(null), isLoading: shallowRef(false), error: shallowRef(null) }),
}));

import AutomationDetailPanel from "../AutomationDetailPanel.vue";
import AutomationsNavPanel from "../AutomationsNavPanel.vue";
import { useAutomationsNav } from "@/composables/use-automations-nav";
import { NEW_SESSION_DEFAULTS_KEY } from "@/composables/use-new-session-defaults";

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

const fleet: ScannedRepository = { name: "weave-fleet", path: "/home/me/source/weave-fleet", parentRoot: "/home/me/source" };

function agentStep(id: string, title: string, extra: Partial<Workflow["steps"][number]> = {}): Workflow["steps"][number] {
  return {
    id, title, kind: "agent", agent: null, model: "strong", effort: null, skill: null, optional: false, optionalHint: null,
    outcomes: ["done"], routes: {}, maxLoops: null, ask: null, choices: [], finishYou: false, finishAgent: false, writes: [],
    ...extra,
  };
}

const buildAFeature: Workflow = {
  id: "builtin:build-a-feature",
  builtIn: true,
  file: null,
  name: "Build a feature",
  description: "Turns a sentence into a reviewed pull request.",
  placeholder: null,
  startsFrom: "sentence",
  runsIn: "new-worktree",
  steps: [
    agentStep("design", "Design", { optional: true, optionalHint: "For UI and new features" }),
    agentStep("plan", "Plan"),
    agentStep("verify", "Check it runs", { optional: true }),
  ],
  errors: [],
};
const deps: Workflow = { ...buildAFeature, id: "repo:deps", builtIn: false, file: ".weave/workflows/deps.yaml", name: "Weekly dependency bump", steps: [agentStep("bump", "Bump")] };
const broken: Workflow = { ...deps, id: "repo:broken", file: ".weave/workflows/broken.yaml", name: "Broken", errors: [".weave/workflows/broken.yaml, line 3: steps is empty."] };
const library: WorkflowLibrary = { repository: fleet.path, repositoryName: "weave-fleet", workflows: [buildAFeature, deps, broken] };

const bump: Automation = {
  id: "a-wf",
  name: "Weekly dependency bump",
  prompt: "Bump the client's dependencies",
  triggerType: "schedule",
  triggerConfig: "0 9 * * 1",
  maxConcurrentRuns: 1,
  maxRunsPerHour: 10,
  timeoutMinutes: 30,
  isEnabled: true,
  workspaceId: fleet.path,
  model: null,
  agent: null,
  createdAt: "2026-09-01T09:00:00Z",
  updatedAt: null,
  targetType: "workflow",
  timeZone: "Africa/Johannesburg",
  isolation: "worktree",
  baseBranch: null,
  harnessType: null,
  workflowId: "builtin:build-a-feature",
  workflowSteps: ["design"],
  nextRunAt: null,
  lastRun: null,
};

let list: Automation[] = [];
let runs: AutomationRun[] = [];
const sent: { method: string; url: string; body: Record<string, unknown> }[] = [];
const libraryRequests: string[] = [];

function serve(url: string, init?: RequestInit): Response {
  const method = init?.method ?? "GET";
  const body = init?.body ? (JSON.parse(String(init.body)) as Record<string, unknown>) : {};
  if (method !== "GET") sent.push({ method, url, body });
  if (method === "GET" && url.startsWith("/api/workflows?")) {
    libraryRequests.push(url);
    return json(library);
  }
  if (method === "GET" && url.startsWith("/api/workflows/runs")) return json([]);
  if (method === "GET" && url === "/api/automations") return json({ automations: list });
  if (method === "GET" && /\/api\/automations\/[^/]+\/runs$/.test(url)) return json({ runs });
  if (method === "POST" && url === "/api/automations") {
    const automation = { ...bump, ...body, id: `a${list.length + 1}`, isEnabled: true, lastRun: null } as Automation;
    list = [...list, automation];
    return json(automation, 201);
  }
  const item = url.match(/^\/api\/automations\/([^/]+)$/);
  if (method === "PUT" && item) {
    list = list.map((a) => (a.id === item[1] ? { ...a, ...body } as Automation : a));
    return json(list.find((a) => a.id === item[1]));
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

describe("An automation that runs a workflow", () => {
  let wrapper: VueWrapper;

  async function mountScreen(): Promise<void> {
    // Menus render into the body, as in the app.
    wrapper = mount(Screen, { attachTo: document.body, global: { stubs: { teleport: false } } });
    await flushPromises();
  }

  /** Opens a dropdown chip and picks one of its items. */
  async function choose(chip: string, item: string): Promise<void> {
    await wrapper.find(`[data-testid='${chip}']`).trigger("click");
    await flushPromises();
    const option = document.querySelector<HTMLElement>(`[data-testid='${item}']`);
    expect(option, `${item} in ${chip}`).not.toBeNull();
    option!.click();
    await flushPromises();
  }

  beforeEach(() => {
    vi.useFakeTimers({ toFake: ["Date"] });
    vi.setSystemTime(new Date(2026, 8, 15, 10, 0, 0));
    setActivePinia(createPinia());
    localStorage.setItem(NEW_SESSION_DEFAULTS_KEY, JSON.stringify({
      lastFolder: { kind: "repository", path: fleet.path },
      recentFolders: [{ kind: "repository", path: fleet.path }],
      workspaceByRepository: {},
    }));
    repositories.value = [fleet];
    workflowsOn.value = true;
    list = [];
    runs = [];
    sent.length = 0;
    libraryRequests.length = 0;
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
    vi.useRealTimers();
  });

  it("switches the composer to a workflow, lists the folder's Library, and sends the message as the request", async () => {
    await mountScreen();
    await wrapper.find("[data-testid='new-automation']").trigger("click");
    await flushPromises();
    await wrapper.find("[data-testid='automation-message']").setValue("Every Monday at 9am, bump the client's dependencies");
    await flushPromises();

    expect(wrapper.find("[data-testid='automation-target-chip']").text()).toContain("a session");
    await choose("automation-target-chip", "automation-target-workflow");

    // The Library for the chosen folder, with Build a feature picked first; its optional steps are switches.
    expect(libraryRequests).toEqual([`/api/workflows?directory=${encodeURIComponent(fleet.path)}`]);
    expect(wrapper.find("[data-testid='automation-target-chip']").text()).toContain("a workflow");
    expect(wrapper.find("[data-testid='automation-workflow-chip']").text()).toContain("Build a feature");
    expect(wrapper.find("[data-testid='automation-runs-in-chip']").exists()).toBe(false);
    const design = wrapper.find("[data-testid='automation-workflow-step-design']");
    expect(design.text()).toMatch(/Design:\s*off/);
    await design.trigger("click");
    expect(design.attributes("aria-checked")).toBe("true");
    expect(wrapper.find("[data-testid='new-session-harness']").exists()).toBe(true);

    const plan = wrapper.find("[data-testid='automation-plan']").text();
    expect(plan).toContain("Each run: Build a feature in a new worktree of ~/source/weave-fleet, with your message as the request.");
    expect(plan).toContain("Check with me is off");

    await wrapper.find("[data-testid='automation-submit']").trigger("click");
    await flushPromises();

    expect(sent[0]).toMatchObject({
      method: "POST",
      url: "/api/automations",
      body: {
        prompt: "Bump the client's dependencies",
        triggerType: "schedule",
        triggerConfig: "0 9 * * 1",
        targetType: "workflow",
        workflowId: "builtin:build-a-feature",
        workflowSteps: ["design"],
        workspaceId: fleet.path,
        isolation: "worktree",
        model: null,
        agent: null,
        harnessType: null,
      },
    });
  });

  it("lists the repo's own workflows after the built-ins, and a file with errors can't be picked", async () => {
    await mountScreen();
    await wrapper.find("[data-testid='new-automation']").trigger("click");
    await flushPromises();
    await choose("automation-target-chip", "automation-target-workflow");
    await choose("automation-workflow-chip", "automation-workflow-repo:deps");

    expect(wrapper.find("[data-testid='automation-workflow-chip']").text()).toContain("Weekly dependency bump");
    // Bump has no optional steps, and the ones Design had are gone with it.
    expect(wrapper.find("[data-testid='automation-workflow-step-design']").exists()).toBe(false);
    await wrapper.find("[data-testid='automation-workflow-chip']").trigger("keydown", { key: "Enter" });
    await flushPromises();
    const brokenOption = document.querySelector<HTMLElement>("[data-testid='automation-workflow-repo:broken']");
    expect(brokenOption?.getAttribute("data-disabled")).not.toBeNull();
    expect(brokenOption?.textContent).toContain("The file has errors");
  });

  it("doesn't offer a workflow when Workflows are off", async () => {
    workflowsOn.value = false;
    await mountScreen();
    await wrapper.find("[data-testid='new-automation']").trigger("click");
    await flushPromises();

    expect(wrapper.find("[data-testid='automation-target-chip']").exists()).toBe(false);
    expect(wrapper.find("[data-testid='automation-runs-in-chip']").exists()).toBe(true);
    expect(libraryRequests).toEqual([]);
  });

  it("warns on an automation that runs a workflow while Workflows are off", async () => {
    workflowsOn.value = false;
    list = [bump];
    runs = [{
      id: "r1", automationId: "a-wf", trigger: "schedule", scheduledFor: "2026-09-14T07:00:00.0000000Z", startedAt: "2026-09-14T07:00:01.0000000Z",
      state: "skipped", sessionId: null, instanceId: null, error: "Skipped: Workflows are turned off in Settings.",
    }];
    await mountScreen();
    useAutomationsNav().setActiveAutomation("a-wf");
    await flushPromises();

    expect(wrapper.find("[data-testid='automation-workflows-off']").text())
      .toContain("This automation runs a workflow, and Workflows are turned off in Settings. Its runs are skipped until they're back on.");
    const row = wrapper.find("[data-testid='automation-run']");
    expect(row.text()).toContain("Skipped");
    expect(row.text()).toContain("Skipped: Workflows are turned off in Settings.");
    // Its workflow still shows by name on the chip, though the Library can't load.
    expect(wrapper.find("[data-testid='automation-workflow-chip']").text()).toContain("build-a-feature");
  });

  it("shows a run that waits on you as Needs you, and opens it", async () => {
    list = [bump];
    runs = [{
      id: "r1", automationId: "a-wf", trigger: "schedule", scheduledFor: "2026-09-14T07:00:00.0000000Z", startedAt: "2026-09-14T07:00:01.0000000Z",
      state: "waiting", sessionId: "step-1", instanceId: null, error: null, workflowRunId: "run-1",
    }, {
      id: "r2", automationId: "a-wf", trigger: "schedule", scheduledFor: "2026-09-21T07:00:00.0000000Z", startedAt: "2026-09-21T07:00:01.0000000Z",
      state: "skipped", sessionId: null, instanceId: null, error: "Skipped: the last run is still waiting on you (Approve the plan).",
    }];
    await mountScreen();
    useAutomationsNav().setActiveAutomation("a-wf");
    await flushPromises();

    const [waiting, skipped] = wrapper.findAll("[data-testid='automation-run']");
    expect(waiting.text()).toContain("Needs you");
    expect(waiting.text()).toContain("Open run →");
    expect(skipped.text()).toContain("Skipped: the last run is still waiting on you (Approve the plan).");
    await waiting.trigger("click");
    expect(navigate).toHaveBeenCalledWith(expect.objectContaining({ to: "/sessions/$id", params: { id: "step-1" } }));
  });

  it("starts from the Library's Repeat on a schedule…, with the Run box's choices", async () => {
    useAutomationsNav().startCreateFromWorkflow({
      workflowId: "builtin:build-a-feature",
      folder: fleet.path,
      request: "Bump the client's dependencies",
      optionalSteps: ["verify"],
      baseBranch: "main",
      harnessType: "opencode2",
    });
    await mountScreen();

    expect((wrapper.find("[data-testid='automation-message']").element as HTMLTextAreaElement).value).toBe("Bump the client's dependencies");
    expect(wrapper.find("[data-testid='automation-target-chip']").text()).toContain("a workflow");
    expect(wrapper.find("[data-testid='automation-workflow-chip']").text()).toContain("Build a feature");
    expect(wrapper.find("[data-testid='automation-workflow-step-verify']").attributes("aria-checked")).toBe("true");
    expect(wrapper.find("[data-testid='automation-workflow-step-design']").attributes("aria-checked")).toBe("false");
    expect(wrapper.find("[data-testid='new-session-harness']").text()).toContain("OpenCode 2");
    // When is still to add.
    expect(wrapper.find("[data-testid='automation-plan']").text()).toContain("When should it run?");
  });
});
