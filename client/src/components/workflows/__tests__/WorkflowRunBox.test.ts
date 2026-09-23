import { flushPromises, mount } from "@vue/test-utils";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { ref } from "vue";

const { apiFetchMock } = vi.hoisted(() => ({ apiFetchMock: vi.fn() }));
vi.mock("@/lib/api-client", () => ({ apiFetch: apiFetchMock }));
vi.mock("@tanstack/vue-router", () => ({ useRouter: () => ({ navigate: vi.fn() }) }));
vi.mock("@/composables/use-signalr-socket", () => ({ onGlobalEvent: () => () => {} }));
vi.mock("@/composables/use-built-in-skills", () => ({ useBuiltInSkills: () => ({ skills: ref([]) }) }));
vi.mock("@/composables/use-enabled-harnesses", () => {
  const openCode = { type: "opencode", displayName: "OpenCode", capabilities: { supportsWorkflowSteps: true } };
  return { useEnabledHarnesses: () => ({ harnesses: ref([openCode]), enabledHarnesses: ref([openCode]), defaultHarnessType: ref("opencode") }) };
});
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
vi.mock("@/composables/use-settings-nav", () => ({ useSettingsNav: () => ({ setActiveSection: vi.fn() }) }));
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
});
