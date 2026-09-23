import { flushPromises, mount } from "@vue/test-utils";
import { beforeEach, describe, expect, it, vi } from "vitest";

const { apiFetchMock } = vi.hoisted(() => ({ apiFetchMock: vi.fn() }));
vi.mock("@/lib/api-client", () => ({ apiFetch: apiFetchMock }));
vi.mock("@tanstack/vue-router", () => ({ useRouter: () => ({ navigate: vi.fn() }) }));
vi.mock("@/composables/use-signalr-socket", () => ({ onGlobalEvent: () => () => {} }));

import WorkflowStepper from "@/components/workflows/WorkflowStepper.vue";
import type { WorkflowRun } from "@/lib/workflows";
import { useWorkflowsStore } from "@/stores/workflows";
import { buildRun, respond, runSession, runStep } from "./workflow-fixtures";

/** Implement running on its own; Review still to come. */
function implementRun(overrides: Partial<WorkflowRun> = {}): WorkflowRun {
  return buildRun({
    currentStepId: "implement",
    withYou: null,
    steps: [
      runStep({ id: "plan", title: "Plan", state: "done", visits: 1, sessionId: "s2" }),
      runStep({ id: "implement", title: "Implement", state: "running", visits: 1, sessionId: "s3" }),
      runStep({ id: "review", title: "Review", withYou: false }),
    ],
    sessions: [runSession({ sessionId: "s2", stepId: "plan" }), runSession({ sessionId: "s3", stepId: "implement" })],
    ...overrides,
  });
}

describe("WorkflowStepper", () => {
  beforeEach(() => {
    apiFetchMock.mockReset();
  });

  it("marks the steps the user finishes with a person, until they're done", () => {
    useWorkflowsStore().upsert(buildRun({
      steps: [
        runStep({ id: "design", title: "Design", state: "done", visits: 1, sessionId: "s1", withYou: true }),
        runStep({ id: "plan", title: "Plan", state: "running", visits: 1, sessionId: "s2" }),
        runStep({ id: "implement", title: "Implement", withYou: true }),
      ],
      sessions: [runSession({ sessionId: "s1", stepId: "design" }), runSession({ sessionId: "s2", stepId: "plan" })],
    }));
    const wrapper = mount(WorkflowStepper, { props: { sessionId: "s2" } });

    const marked = wrapper.findAll('[data-testid="workflow-step-with-you"]');
    expect(marked).toHaveLength(1);
    expect(marked[0].element.closest("button")?.textContent).toContain("Implement");
  });

  it("switches Check with me from the header", async () => {
    useWorkflowsStore().upsert(implementRun());
    apiFetchMock.mockImplementation(() => respond(implementRun({ checkWithMe: true, updatedAt: "2026-09-23T10:06:00Z" })));
    const wrapper = mount(WorkflowStepper, { props: { sessionId: "s3" } });

    const toggle = wrapper.get('[data-testid="workflow-check-with-me"]');
    expect(toggle.attributes("aria-checked")).toBe("false");
    await toggle.trigger("click");
    await flushPromises();

    const [path, init] = apiFetchMock.mock.calls[0] as [string, RequestInit];
    expect(path).toBe("/api/workflows/runs/run-1/check-with-me");
    expect(init.method).toBe("PUT");
    expect(JSON.parse(init.body as string)).toEqual({ on: true });
    expect(wrapper.get('[data-testid="workflow-check-with-me"]').attributes("aria-checked")).toBe("true");
  });

  it("says the change applies from the next step when it differs from the running one", () => {
    useWorkflowsStore().upsert(implementRun({ checkWithMe: true }));
    const on = mount(WorkflowStepper, { props: { sessionId: "s3" } });
    expect(on.get('[data-testid="workflow-check-with-me-note"]').text())
      .toBe("Check with me is on from Review. This step started on its own, so it still moves on when the agent reports it's done.");

    const withYou = implementRun({ checkWithMe: false, updatedAt: "2026-09-23T10:07:00Z" });
    withYou.steps[1] = { ...withYou.steps[1], withYou: true };
    useWorkflowsStore().upsert(withYou);
    const off = mount(WorkflowStepper, { props: { sessionId: "s3" } });
    expect(off.get('[data-testid="workflow-check-with-me-note"]').text())
      .toBe("Check with me is off from Review. You still finish this step, because it started with you.");
  });

  it("says nothing when the running step already matches, and has no switch once the run is over", () => {
    useWorkflowsStore().upsert(implementRun());
    expect(mount(WorkflowStepper, { props: { sessionId: "s3" } }).find('[data-testid="workflow-check-with-me-note"]').exists()).toBe(false);

    useWorkflowsStore().upsert(implementRun({ status: "done", updatedAt: "2026-09-23T10:08:00Z" }));
    expect(mount(WorkflowStepper, { props: { sessionId: "s3" } }).find('[data-testid="workflow-check-with-me"]').exists()).toBe(false);
  });
});
