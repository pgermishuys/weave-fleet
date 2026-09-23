import { flushPromises, mount } from "@vue/test-utils";
import { beforeEach, describe, expect, it, vi } from "vitest";

const { apiFetchMock, navigateMock } = vi.hoisted(() => ({ apiFetchMock: vi.fn(), navigateMock: vi.fn() }));
vi.mock("@/lib/api-client", () => ({ apiFetch: apiFetchMock }));
vi.mock("@tanstack/vue-router", () => ({ useRouter: () => ({ navigate: navigateMock }) }));
vi.mock("@/composables/use-signalr-socket", () => ({ onGlobalEvent: () => () => {} }));

import WorkflowRunCard from "@/components/workflows/WorkflowRunCard.vue";
import type { WorkflowRun } from "@/lib/workflows";
import { useCanvasesStore } from "@/stores/canvases";
import { useSidebarStore } from "@/stores/sidebar";
import { useWorkflowsStore } from "@/stores/workflows";
import { buildRun, runSession, runStep } from "./workflow-fixtures";

/** Build a feature waiting at Approve the plan, with Implement and Review still to run before Open the pull request. */
function approvalRun(): WorkflowRun {
  return buildRun({
    id: "run-approve",
    status: "waiting",
    currentStepId: "ok-plan",
    withYou: null,
    steps: [
      runStep({ id: "plan", title: "Plan", state: "done", visits: 1, sessionId: "s2", outcome: "ready", outcomes: ["ready"] }),
      runStep({ id: "ok-plan", title: "Approve the plan", kind: "you", state: "waiting", visits: 1 }),
      runStep({ id: "implement", title: "Implement", outcomes: ["done"] }),
      runStep({ id: "review", title: "Review", outcomes: ["pass", "changes"], maxLoops: 2 }),
    ],
    sessions: [runSession({ sessionId: "s2", stepId: "plan", outcome: "ready", files: [".weave/plans/sheet.md"], filesChecked: true })],
    waiting: {
      kind: "you",
      stepId: "ok-plan",
      stepTitle: "Approve the plan",
      message: "Build it this way?",
      question: "Build it this way?",
      sessionId: "s2",
      choices: [
        { id: "choice:0", label: "Approve", note: false, to: "implement", involvement: true },
        { id: "choice:1", label: "Send back with a note", note: true, to: "plan", involvement: false },
      ],
      files: [".weave/plans/sheet.md"],
      thenAlone: ["Implement", "Review"],
      nextYouTitle: "Open the pull request",
    },
  });
}

function waitingRun(): WorkflowRun {
  return {
    id: "run-1",
    workflowId: "builtin:build-a-feature",
    workflowName: "Build a feature",
    title: "Keyboard shortcut sheet",
    request: "Press ?",
    status: "waiting",
    currentStepId: "ok-plan",
    result: null,
    repositoryPath: "/repo",
    branch: null,
    baseBranch: null,
    worktreePath: null,
    harnessType: "opencode",
    createdAt: "2026-09-23T10:00:00Z",
    updatedAt: "2026-09-23T10:05:00Z",
    endedAt: null,
    steps: [
      { id: "plan", title: "Plan", kind: "agent", state: "done", visits: 1, optional: false, enabled: true, outcome: "ready", sessionId: "s1", model: null, role: "strong", skill: null, maxLoops: null, finishYou: false, finishAgent: false, withYou: false, outcomes: [] },
      { id: "ok-plan", title: "Approve the plan", kind: "you", state: "waiting", visits: 1, optional: false, enabled: true, outcome: null, sessionId: null, model: null, role: null, skill: null, maxLoops: null, finishYou: false, finishAgent: false, withYou: false, outcomes: [] },
      { id: "implement", title: "Implement", kind: "agent", state: "pending", visits: 0, optional: false, enabled: true, outcome: null, sessionId: null, model: null, role: "standard", skill: null, maxLoops: null, finishYou: false, finishAgent: false, withYou: false, outcomes: [] },
    ],
    sessions: [{ sessionId: "s1", stepId: "plan", visit: 1, outcome: "ready", summary: "Plan.", note: null, withYou: false, files: [], filesChecked: false, filesCommit: null, filesCommitError: null, promptMessageId: null, wrapUpMessageId: null, handOffNote: null }],
    waiting: {
      kind: "you",
      stepId: "ok-plan",
      stepTitle: "Approve the plan",
      message: "Build it this way?",
      question: "Build it this way?",
      sessionId: "s1",
      choices: [
        { id: "choice:0", label: "Approve", note: false, to: "implement" },
        { id: "choice:1", label: "Send back with a note", note: true, to: "plan" },
      ],
    },
  };
}

function respond(body: unknown): Promise<Response> {
  return Promise.resolve(new Response(JSON.stringify(body), { status: 200, headers: { "Content-Type": "application/json" } }));
}

describe("WorkflowRunCard", () => {
  beforeEach(() => {
    apiFetchMock.mockReset();
  });

  it("asks the You step's question in the session before it, and approves", async () => {
    useWorkflowsStore().upsert(waitingRun());
    apiFetchMock.mockImplementation(() => respond({ ...waitingRun(), status: "running", waiting: null, updatedAt: "2026-09-23T10:06:00Z" }));
    const wrapper = mount(WorkflowRunCard, { props: { sessionId: "s1" } });

    expect(wrapper.text()).toContain("Approve the plan");
    expect(wrapper.text()).toContain("Build it this way?");
    await wrapper.get('[data-testid="workflow-card-choice-0"]').trigger("click");
    await flushPromises();

    const [path, init] = apiFetchMock.mock.calls[0] as [string, RequestInit];
    expect(path).toBe("/api/workflows/runs/run-1/answer");
    expect(JSON.parse(init.body as string)).toEqual({ choice: "choice:0", note: null });
    expect(wrapper.find('[data-testid="workflow-card"]').exists()).toBe(false);
  });

  it("sends the plan back with the note the user wrote", async () => {
    useWorkflowsStore().upsert(waitingRun());
    apiFetchMock.mockImplementation(() => respond({ ...waitingRun(), status: "running", waiting: null, updatedAt: "2026-09-23T10:06:00Z" }));
    const wrapper = mount(WorkflowRunCard, { props: { sessionId: "s1" } });

    await wrapper.get('[data-testid="workflow-card-choice-1"]').trigger("click");
    const send = wrapper.get('[data-testid="workflow-card-send-note"]');
    expect(send.attributes("disabled")).toBeDefined();
    await wrapper.get('[data-testid="workflow-card-note"]').setValue("Leave the status bar alone.");
    await wrapper.get('[data-testid="workflow-card-send-note"]').trigger("click");
    await flushPromises();

    const [, init] = apiFetchMock.mock.calls[0] as [string, RequestInit];
    expect(JSON.parse(init.body as string)).toEqual({ choice: "choice:1", note: "Leave the status bar alone." });
  });

  it("offers two ways to approve, and sends how involved the user wants to be", async () => {
    useWorkflowsStore().upsert(approvalRun());
    apiFetchMock.mockImplementation(() => respond({ ...approvalRun(), status: "running", waiting: null, updatedAt: "2026-09-23T10:06:00Z" }));
    const wrapper = mount(WorkflowRunCard, { props: { sessionId: "s2" } });

    expect(wrapper.text()).toContain("Build it this way? sheet.md is open on the right. Then choose how involved you want to be in the rest of the run; you can change it later from the header.");
    const letItRun = wrapper.get('[data-testid="workflow-card-let-it-run"]');
    expect(letItRun.text()).toContain("Approve, let it run");
    expect(letItRun.text()).toContain("Implement and Review go ahead on their own. You're asked again at Open the pull request.");
    expect(wrapper.get('[data-testid="workflow-card-check-with-me"]').text()).toContain("Approve, check with me after each step");
    // Send back with a note and End run stay.
    expect(wrapper.get('[data-testid="workflow-card-choice-1"]').text()).toContain("Send back with a note");
    expect(wrapper.find('[data-testid="workflow-card-end"]').exists()).toBe(true);

    await wrapper.get('[data-testid="workflow-card-check-with-me"]').trigger("click");
    await flushPromises();
    expect(JSON.parse((apiFetchMock.mock.calls[0] as [string, RequestInit])[1].body as string)).toEqual({ choice: "choice:0", note: null, checkWithMe: true });
  });

  it("approves and lets the rest run", async () => {
    useWorkflowsStore().upsert(approvalRun());
    apiFetchMock.mockImplementation(() => respond({ ...approvalRun(), status: "running", waiting: null, updatedAt: "2026-09-23T10:06:00Z" }));
    const wrapper = mount(WorkflowRunCard, { props: { sessionId: "s2" } });

    await wrapper.get('[data-testid="workflow-card-let-it-run"]').trigger("click");
    await flushPromises();
    expect(JSON.parse((apiFetchMock.mock.calls[0] as [string, RequestInit])[1].body as string)).toEqual({ choice: "choice:0", note: null, checkWithMe: false });
  });

  it("opens the plan next to Approve the plan, once", () => {
    useWorkflowsStore().upsert({ ...approvalRun(), id: "run-open" });
    const sidebar = useSidebarStore();
    sidebar.setRightPanelCollapsed(true);
    const canvases = useCanvasesStore();
    const openFile = vi.spyOn(canvases, "openFile");

    mount(WorkflowRunCard, { props: { sessionId: "s2" } });
    mount(WorkflowRunCard, { props: { sessionId: "s2" } });

    expect(openFile).toHaveBeenCalledTimes(1);
    expect(openFile).toHaveBeenCalledWith("s2", ".weave/plans/sheet.md", { keep: true });
    expect(sidebar.rightPanelCollapsed).toBe(false);
  });

  it("offers to move on anyway past a missing file", async () => {
    const run = buildRun({
      id: "run-missing",
      status: "waiting",
      currentStepId: "design",
      withYou: null,
      waiting: {
        kind: "missing-files",
        stepId: "design",
        stepTitle: "Design",
        message: "Design declares docs/design/sheet.html, but it isn't in the run's worktree. Reply to the agent in its session, or move on anyway.",
        question: null,
        sessionId: "s1",
        choices: [{ id: "move-on-anyway", label: "Move on anyway", note: false, to: null }],
        files: ["docs/design/sheet.md", "docs/design/sheet.html"],
      },
    });
    useWorkflowsStore().upsert(run);
    apiFetchMock.mockImplementation(() => respond({ ...run, status: "running", waiting: null, updatedAt: "2026-09-23T10:06:00Z" }));
    const wrapper = mount(WorkflowRunCard, { props: { sessionId: "s1" } });

    expect(wrapper.text()).toContain("Design needs you");
    expect(wrapper.text()).toContain("Design declares docs/design/sheet.html, but it isn't in the run's worktree.");
    const anyway = wrapper.get('[data-testid="workflow-card-choice-0"]');
    expect(anyway.text()).toBe("Move on anyway");
    await anyway.trigger("click");
    await flushPromises();
    expect(JSON.parse((apiFetchMock.mock.calls[0] as [string, RequestInit])[1].body as string)).toEqual({ choice: "move-on-anyway", note: null });
  });

  it("says the files were checked once the run moved on", () => {
    useWorkflowsStore().upsert(buildRun({
      id: "run-moved",
      currentStepId: "plan",
      withYou: null,
      sessions: [
        runSession({ sessionId: "s1", stepId: "design", outcome: "ready", withYou: true, files: ["docs/design/sheet.md"], filesChecked: true }),
        runSession({ sessionId: "s2", stepId: "plan" }),
      ],
    }));
    const wrapper = mount(WorkflowRunCard, { props: { sessionId: "s1" } });

    expect(wrapper.get('[data-testid="workflow-card-files-checked"]').text()).toBe("Files checked: sheet.md");
    expect(wrapper.text()).toContain("Design finished: ready.");
  });

  it("says Fleet committed the files it checked", () => {
    useWorkflowsStore().upsert(buildRun({
      id: "run-committed",
      currentStepId: "plan",
      withYou: null,
      sessions: [
        runSession({ sessionId: "s1", stepId: "design", outcome: "ready", files: ["docs/design/sheet.md", "docs/design/sheet.html"], filesChecked: true, filesCommit: "a1b2c3d" }),
        runSession({ sessionId: "s2", stepId: "plan" }),
      ],
    }));
    const wrapper = mount(WorkflowRunCard, { props: { sessionId: "s1" } });

    expect(wrapper.get('[data-testid="workflow-card-files-checked"]').text()).toBe("Files checked and committed as a1b2c3d: sheet.md and sheet.html");
    expect(wrapper.find('[data-testid="workflow-card-commit-failed"]').exists()).toBe(false);
  });

  it("says why Fleet couldn't commit the files, and the run still moved on", () => {
    useWorkflowsStore().upsert(buildRun({
      id: "run-not-committed",
      currentStepId: "plan",
      withYou: null,
      sessions: [
        runSession({ sessionId: "s1", stepId: "design", outcome: "ready", files: ["docs/design/sheet.md"], filesChecked: true, filesCommitError: "no email was given and auto-detection is disabled" }),
        runSession({ sessionId: "s2", stepId: "plan" }),
      ],
    }));
    const wrapper = mount(WorkflowRunCard, { props: { sessionId: "s1" } });

    expect(wrapper.get('[data-testid="workflow-card-files-checked"]').text()).toBe("Files checked: sheet.md");
    expect(wrapper.get('[data-testid="workflow-card-commit-failed"]').text()).toBe("Fleet couldn't commit it: no email was given and auto-detection is disabled");
    expect(wrapper.text()).toContain("Plan");
  });

  it("shows nothing in a session the run isn't waiting in", () => {
    useWorkflowsStore().upsert(waitingRun());
    const wrapper = mount(WorkflowRunCard, { props: { sessionId: "other" } });

    expect(wrapper.find('[data-testid="workflow-card"]').exists()).toBe(false);
  });
});
