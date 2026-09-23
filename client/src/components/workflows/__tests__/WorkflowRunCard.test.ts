import { flushPromises, mount } from "@vue/test-utils";
import { beforeEach, describe, expect, it, vi } from "vitest";

const { apiFetchMock, navigateMock } = vi.hoisted(() => ({ apiFetchMock: vi.fn(), navigateMock: vi.fn() }));
vi.mock("@/lib/api-client", () => ({ apiFetch: apiFetchMock }));
vi.mock("@tanstack/vue-router", () => ({ useRouter: () => ({ navigate: navigateMock }) }));
vi.mock("@/composables/use-signalr-socket", () => ({ onGlobalEvent: () => () => {} }));

import WorkflowRunCard from "@/components/workflows/WorkflowRunCard.vue";
import type { WorkflowRun } from "@/lib/workflows";
import { useWorkflowsStore } from "@/stores/workflows";

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
      { id: "plan", title: "Plan", kind: "agent", state: "done", visits: 1, optional: false, enabled: true, outcome: "ready", sessionId: "s1", model: null, role: "strong", skill: null, maxLoops: null },
      { id: "ok-plan", title: "Approve the plan", kind: "you", state: "waiting", visits: 1, optional: false, enabled: true, outcome: null, sessionId: null, model: null, role: null, skill: null, maxLoops: null },
      { id: "implement", title: "Implement", kind: "agent", state: "pending", visits: 0, optional: false, enabled: true, outcome: null, sessionId: null, model: null, role: "standard", skill: null, maxLoops: null },
    ],
    sessions: [{ sessionId: "s1", stepId: "plan", visit: 1, outcome: "ready", summary: "Plan.", note: null }],
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

  it("shows nothing in a session the run isn't waiting in", () => {
    useWorkflowsStore().upsert(waitingRun());
    const wrapper = mount(WorkflowRunCard, { props: { sessionId: "other" } });

    expect(wrapper.find('[data-testid="workflow-card"]').exists()).toBe(false);
  });
});
