import { flushPromises, mount } from "@vue/test-utils";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { ref } from "vue";

const { apiFetchMock, harnessList, setActiveSectionMock } = vi.hoisted(() => {
  const harness = (type: string, displayName: string, supportsWorkflowSteps: boolean, ready = true) =>
    ({ type, displayName, available: ready, userEnabled: true, capabilities: { supportsWorkflowSteps } });
  const all = {
    opencode: harness("opencode", "OpenCode", true),
    opencode2: harness("opencode2", "OpenCode 2", true),
    claudeCode: harness("claude-code", "Claude Code", true),
    copilot: harness("copilot", "GitHub Copilot", true),
    pi: harness("pi", "Pi", false),
  };
  return {
    apiFetchMock: vi.fn(),
    setActiveSectionMock: vi.fn(),
    harnessList: { all, harnesses: [all.opencode] as ReturnType<typeof harness>[], enabled: [all.opencode] as ReturnType<typeof harness>[], defaultType: "opencode" },
  };
});
vi.mock("@/lib/api-client", () => ({ apiFetch: apiFetchMock }));
vi.mock("@tanstack/vue-router", () => ({ useRouter: () => ({ navigate: vi.fn() }) }));
vi.mock("@/composables/use-signalr-socket", () => ({ onGlobalEvent: () => () => {} }));
vi.mock("@/composables/use-built-in-skills", () => ({ useBuiltInSkills: () => ({ skills: ref([]) }) }));
vi.mock("@/composables/use-enabled-harnesses", () => ({
  useEnabledHarnesses: () => ({
    harnesses: ref(harnessList.harnesses),
    enabledHarnesses: ref(harnessList.enabled),
    defaultHarnessType: ref(harnessList.defaultType),
  }),
}));
vi.mock("@/composables/use-harness-catalog", () => ({ useHarnessCatalog: () => ({ models: ref([]), isSupported: ref(false) }) }));
vi.mock("@/composables/use-model-roles", async (original) => ({
  ...(await original<typeof import("@/composables/use-model-roles")>()),
  useModelRoles: () => ({ choiceFor: () => ({ model: null, effort: null }) }),
}));
vi.mock("@/composables/use-new-session-defaults", () => ({ useNewSessionDefaults: () => ({ recentFolders: () => [], initialFolder: () => null }) }));
vi.mock("@/composables/use-repositories", () => ({
  useRepositories: () => ({ repositories: ref([]), scannedAt: ref("2026-09-23T10:00:00Z"), error: ref(null), refresh: vi.fn() }),
}));
vi.mock("@/composables/use-repository-detail", () => ({ useRepositoryDetail: () => ({ detail: ref(null), isLoading: ref(false) }) }));
vi.mock("@/composables/use-settings-nav", () => ({ useSettingsNav: () => ({ setActiveSection: setActiveSectionMock }) }));
vi.mock("@/composables/use-projects", () => ({
  useProjects: () => ({ projects: ref([{ id: "proj-1", name: "Dependencies", type: "standard" }]) }),
}));
vi.mock("@/composables/use-workflows-nav", () => ({
  useWorkflowsNav: () => ({
    repositoryPath: ref("/repo"),
    folder: ref({ kind: "repository", path: "/repo", name: "repo" }),
    hasChosenFolder: ref(true),
    setFolder: vi.fn(),
  }),
}));

import WorkflowRunBox from "@/components/workflows/WorkflowRunBox.vue";
import type { Workflow } from "@/lib/workflows";
import { buildRun, respond } from "./workflow-fixtures";

const workflow: Workflow = {
  id: "builtin:build-a-feature",
  builtIn: true,
  file: null,
  name: "Build a feature",
  description: null,
  placeholder: "What should it build?",
  startsFrom: "sentence",
  runsIn: "new-worktree",
  steps: [{
    id: "plan", title: "Plan", kind: "agent", agent: "plan", model: "strong", effort: null, skill: null, optional: false, optionalHint: null,
    outcomes: ["ready"], routes: {}, maxLoops: null, ask: null, choices: [], finishYou: false, finishAgent: false, writes: [".weave/plans/{{slug}}.md"],
  }],
  errors: [],
};

const stubs = { FolderPicker: true, BasePicker: true, HarnessPicker: true, ModelSelector: true };

describe("WorkflowRunBox", () => {
  beforeEach(() => {
    apiFetchMock.mockReset();
    apiFetchMock.mockImplementation(() => respond(buildRun()));
    setActiveSectionMock.mockReset();
    harnessList.harnesses = [harnessList.all.opencode];
    harnessList.enabled = [harnessList.all.opencode];
    harnessList.defaultType = "opencode";
  });

  it("starts a run with Check with me after each step switched on", async () => {
    const wrapper = mount(WorkflowRunBox, { props: { workflow }, global: { stubs } });

    const toggle = wrapper.get('[data-testid="workflow-check-with-me-start"]');
    expect(toggle.attributes("aria-checked")).toBe("false");
    expect(toggle.text()).toMatch(/Check with me:\s*off/);
    await toggle.trigger("click");
    expect(toggle.attributes("aria-checked")).toBe("true");
    expect(toggle.text()).toMatch(/Check with me:\s*on/);

    await wrapper.get('[data-testid="workflow-request"]').setValue("Press ? to see every keyboard shortcut");
    await wrapper.get('[data-testid="workflow-run"]').trigger("click");
    await flushPromises();

    const [path, init] = apiFetchMock.mock.calls[0] as [string, RequestInit];
    expect(path).toBe("/api/workflows/runs");
    expect(JSON.parse(init.body as string)).toMatchObject({ workflowId: "builtin:build-a-feature", directory: "/repo", checkWithMe: true });
  });

  it("leaves it off unless it's switched on", async () => {
    const wrapper = mount(WorkflowRunBox, { props: { workflow }, global: { stubs } });

    await wrapper.get('[data-testid="workflow-request"]').setValue("Press ?");
    await wrapper.get('[data-testid="workflow-run"]').trigger("click");
    await flushPromises();

    expect(JSON.parse((apiFetchMock.mock.calls[0] as [string, RequestInit])[1].body as string)).toMatchObject({ checkWithMe: false });
  });

  it("sends no project by default (Scratch)", async () => {
    const wrapper = mount(WorkflowRunBox, { props: { workflow }, global: { stubs } });

    expect(wrapper.get('[data-testid="workflow-project"]').text()).toContain("Scratch");

    await wrapper.get('[data-testid="workflow-request"]').setValue("Press ?");
    await wrapper.get('[data-testid="workflow-run"]').trigger("click");
    await flushPromises();

    expect(JSON.parse((apiFetchMock.mock.calls[0] as [string, RequestInit])[1].body as string)).toMatchObject({ projectId: null });
  });

  it("sends the chosen project", async () => {
    const wrapper = mount(WorkflowRunBox, { props: { workflow }, global: { stubs: { ...stubs, ProjectPicker: true } } });

    await wrapper.findComponent({ name: "ProjectPicker" }).vm.$emit("update:modelValue", "proj-1");
    await wrapper.get('[data-testid="workflow-request"]').setValue("Press ?");
    await wrapper.get('[data-testid="workflow-run"]').trigger("click");
    await flushPromises();

    expect(JSON.parse((apiFetchMock.mock.calls[0] as [string, RequestInit])[1].body as string)).toMatchObject({ projectId: "proj-1" });
  });

  it("says no harness that runs workflows is ready, by name, and points to Settings → Harnesses", async () => {
    const { opencode, opencode2, claudeCode, copilot, pi } = harnessList.all;
    // OpenCode 2 is the default but isn't installed; only Pi, which can't run workflows, is ready.
    harnessList.harnesses = [{ ...opencode2, available: false }, { ...opencode, available: false }, { ...claudeCode, userEnabled: false }, { ...copilot, available: false }, pi];
    harnessList.enabled = [pi];
    harnessList.defaultType = "opencode2";
    const wrapper = mount(WorkflowRunBox, { props: { workflow }, global: { stubs } });

    const warning = wrapper.get('[data-testid="workflow-no-harness"]');
    expect(warning.text()).toContain(
      "Workflows run on OpenCode 2, OpenCode, Claude Code or GitHub Copilot, and none of them is installed and on.");
    expect(warning.text()).not.toContain("opencode2");
    expect(warning.text()).not.toContain("Pick");
    await warning.get("button").trigger("click");
    expect(setActiveSectionMock).toHaveBeenCalledWith("harnesses");
    expect(wrapper.get('[data-testid="workflow-run"]').attributes("disabled")).toBeDefined();
  });

  it("says nothing about harnesses while one that runs workflows is ready", () => {
    harnessList.harnesses = [harnessList.all.pi, harnessList.all.opencode];
    harnessList.enabled = [harnessList.all.pi, harnessList.all.opencode];
    harnessList.defaultType = "pi";
    const wrapper = mount(WorkflowRunBox, { props: { workflow }, global: { stubs } });

    expect(wrapper.find('[data-testid="workflow-no-harness"]').exists()).toBe(false);
  });
});
