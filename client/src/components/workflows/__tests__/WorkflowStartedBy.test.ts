import { flushPromises, mount } from "@vue/test-utils";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { defineComponent, ref } from "vue";

const { navigate } = vi.hoisted(() => ({ navigate: vi.fn() }));
vi.mock("@tanstack/vue-router", () => ({ useRouter: () => ({ navigate }) }));
vi.mock("@/lib/api-client", () => ({ apiFetch: vi.fn() }));
vi.mock("@/composables/use-signalr-socket", () => ({ onGlobalEvent: () => () => {} }));
vi.mock("@/composables/use-enabled-harnesses", () => ({ useEnabledHarnesses: () => ({ defaultHarnessType: ref("opencode") }) }));
vi.mock("@/composables/use-model-roles", () => ({ useModelRoles: () => ({ choiceFor: () => ({ model: null, effort: null }) }) }));

import WorkflowDetailPanel from "@/components/workflows/WorkflowDetailPanel.vue";
import WorkflowRunGroup from "@/components/workflows/WorkflowRunGroup.vue";
import WorkflowStepper from "@/components/workflows/WorkflowStepper.vue";
import { useAutomationsNav } from "@/composables/use-automations-nav";
import type { SessionListItem } from "@/api/client";
import type { Workflow } from "@/lib/workflows";
import { useWorkflowsStore } from "@/stores/workflows";
import { buildRun, runSession } from "./workflow-fixtures";

const workflow: Workflow = {
  id: "builtin:build-a-feature",
  builtIn: true,
  file: null,
  name: "Build a feature",
  description: null,
  placeholder: null,
  startsFrom: "sentence",
  runsIn: "new-worktree",
  steps: [],
  errors: [],
};

const nav = {
  activeWorkflow: ref<Workflow | null>(workflow),
  activeWorkflowId: ref(workflow.id),
  repositoryPath: ref<string | null>("/repo"),
  libraryError: ref<string | null>(null),
  library: ref(null),
  showingRuns: ref(true),
  creating: ref(null),
  unsavedWorkflowId: ref<string | null>(null),
  setLeaveGuard: vi.fn(),
  showRuns: vi.fn(),
  reload: vi.fn(),
  startCreate: vi.fn(),
  endCreate: vi.fn(),
};
vi.mock("@/composables/use-workflows-nav", () => ({ useWorkflowsNav: () => nav }));
// The designer opens only for a repo's own workflow; these are built-ins.
vi.mock("@/composables/use-workflow-editor", () => ({
  useWorkflowEditor: () => ({
    file: ref(null), directory: ref(null), loadError: ref(null), isDirty: ref(false), draft: ref(null),
    open: vi.fn(), adopt: vi.fn(), dispose: vi.fn(),
  }),
}));

const startedBy = { automationId: "a-wf", automationName: "Weekly dependency bump" };
const SessionItemStub = { name: "SessionItem", props: ["session", "label", "active", "stepNote"], template: "<div class=\"step\">{{ label }}</div>" };

/** The Run box, with what's typed and chosen in it. */
const RunBoxStub = defineComponent({
  name: "WorkflowRunBox",
  props: { workflow: { type: Object, required: true } },
  setup(_, { expose }) {
    expose({
      scheduleDraft: () => ({ request: "Bump the client's dependencies", optionalSteps: ["verify"], baseBranch: "main", harnessType: "opencode2" }),
    });
    return {};
  },
  template: "<div />",
});

describe("Started by an automation", () => {
  beforeEach(() => {
    navigate.mockReset();
    useAutomationsNav().clearSelection();
    useAutomationsNav().resetDraft();
    nav.activeWorkflow.value = workflow;
    nav.repositoryPath.value = "/repo";
  });

  it("shows on the run's group in Sessions, and opens the automation", async () => {
    const wrapper = mount(WorkflowRunGroup, {
      props: {
        run: buildRun({ startedBy }),
        steps: [{ session: { session: { id: "s1", title: "Bump · Plan" } } as unknown as SessionListItem, label: "Plan" }],
        activeSessionId: null,
      },
      global: { stubs: { SessionItem: SessionItemStub } },
    });

    const link = wrapper.get("[data-testid='workflow-started-by']");
    expect(link.text()).toBe("Started by Weekly dependency bump");
    await link.trigger("click");

    expect(useAutomationsNav().activeAutomationId.value).toBe("a-wf");
    expect(useAutomationsNav().viewMode.value).toBe("edit");
    expect(navigate).toHaveBeenCalledWith({ to: "/automations" });
    expect(wrapper.emitted("selectSession")).toBeUndefined();
  });

  it("isn't shown on a run started from the Run box", () => {
    const wrapper = mount(WorkflowRunGroup, {
      props: { run: buildRun({ startedBy: null }), steps: [], activeSessionId: null },
      global: { stubs: { SessionItem: SessionItemStub } },
    });

    expect(wrapper.find("[data-testid='workflow-started-by']").exists()).toBe(false);
  });

  it("shows in the run's header above a step's conversation", async () => {
    useWorkflowsStore().upsert(buildRun({ startedBy, sessions: [runSession({ sessionId: "s1", stepId: "design" })] }));
    const wrapper = mount(WorkflowStepper, { props: { sessionId: "s1" } });
    await flushPromises();

    expect(wrapper.get(".wf-stepper__run [data-testid='workflow-started-by']").text()).toBe("Started by Weekly dependency bump");
  });

  it("shows in the Library's recent runs", async () => {
    useWorkflowsStore().upsert(buildRun({ startedBy, workflowId: workflow.id, status: "waiting" }));
    const wrapper = mount(WorkflowDetailPanel, { global: { stubs: { WorkflowRunBox: RunBoxStub } } });
    await flushPromises();

    expect(wrapper.get("[data-testid='workflow-recent-run'] [data-testid='workflow-started-by']").text()).toBe("Started by Weekly dependency bump");
  });
});

describe("Repeat on a schedule… in the Library", () => {
  beforeEach(() => {
    navigate.mockReset();
    useAutomationsNav().clearSelection();
    useAutomationsNav().resetDraft();
    nav.activeWorkflow.value = workflow;
    nav.repositoryPath.value = "/repo";
  });

  it("opens a new automation that runs the workflow, with the Run box's request, steps, base and harness", async () => {
    const wrapper = mount(WorkflowDetailPanel, { global: { stubs: { WorkflowRunBox: RunBoxStub } } });
    await flushPromises();

    await wrapper.get("[data-testid='workflow-repeat-on-schedule']").trigger("click");

    const automations = useAutomationsNav();
    expect(automations.viewMode.value).toBe("create");
    expect(automations.draft).toMatchObject({
      text: "Bump the client's dependencies",
      targetType: "workflow",
      workflowId: "builtin:build-a-feature",
      workflowSteps: ["verify"],
      folder: { kind: "repository", path: "/repo" },
      workspace: { kind: "new" },
      baseBranch: "main",
      harnessType: "opencode2",
      manualWhen: null,
    });
    expect(navigate).toHaveBeenCalledWith({ to: "/automations" });
  });

  it("waits for a repository, and for a file without errors", async () => {
    nav.repositoryPath.value = null;
    const wrapper = mount(WorkflowDetailPanel, { global: { stubs: { WorkflowRunBox: RunBoxStub } } });
    await flushPromises();
    expect(wrapper.get("[data-testid='workflow-repeat-on-schedule']").attributes("disabled")).toBeDefined();

    nav.repositoryPath.value = "/repo";
    nav.activeWorkflow.value = { ...workflow, errors: ["line 3: steps is empty."] };
    await flushPromises();
    expect(wrapper.get("[data-testid='workflow-repeat-on-schedule']").attributes("disabled")).toBeDefined();
  });
});
