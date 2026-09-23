import { flushPromises, mount } from "@vue/test-utils";
import { beforeEach, describe, expect, it, vi } from "vitest";

const { apiFetchMock } = vi.hoisted(() => ({ apiFetchMock: vi.fn() }));
vi.mock("@/lib/api-client", () => ({ apiFetch: apiFetchMock }));
vi.mock("@/composables/use-signalr-socket", () => ({ onGlobalEvent: () => () => {} }));

import WorkflowFinishBar from "@/components/workflows/WorkflowFinishBar.vue";
import type { WorkflowRun } from "@/lib/workflows";
import { useWorkflowsStore } from "@/stores/workflows";
import { buildRun, respond, runSession } from "./workflow-fixtures";

/** Review, with the user, after Implement: Pass goes on, Changes goes back and counts against the loop's maximum. */
function reviewRun(loopsUsed: number): WorkflowRun {
  return buildRun({
    currentStepId: "review",
    checkWithMe: true,
    sessions: [runSession({ sessionId: "s5", stepId: "review", withYou: true })],
    withYou: {
      stepId: "review",
      stepTitle: "Review",
      sessionId: "s5",
      files: [],
      wrappingUp: false,
      outcome: null,
      moves: [
        { outcome: "pass", to: "ok-pr", toTitle: "Open the pull request", back: false, loopsUsed: null, maxLoops: null, allowed: true },
        { outcome: "changes", to: "implement", toTitle: "Implement", back: true, loopsUsed, maxLoops: 2, allowed: loopsUsed < 2 },
      ],
    },
  });
}

describe("WorkflowFinishBar", () => {
  beforeEach(() => {
    apiFetchMock.mockReset();
  });

  it("lets the user finish Design: a note and Move on to Plan, with the files it hands on", async () => {
    useWorkflowsStore().upsert(buildRun());
    apiFetchMock.mockImplementation(() => respond({ ...buildRun({ updatedAt: "2026-09-23T10:06:00Z" }) }));
    const wrapper = mount(WorkflowFinishBar, { props: { sessionId: "s1" } });

    expect(wrapper.text()).toContain("You finish Design");
    expect(wrapper.text()).toContain("The agent can't end this step. Keep talking until it's right.");
    expect(wrapper.text()).toContain("docs/design/sheet.md");
    expect(wrapper.text()).toContain("Then Plan starts with the files, the summary and your note.");
    expect(wrapper.get('[data-testid="workflow-finish-note"]').attributes("placeholder")).toBe("Note for Plan (optional)");

    await wrapper.get('[data-testid="workflow-finish-note"]').setValue("Keep the status-bar shortcuts in the sheet.");
    const move = wrapper.get('[data-testid="workflow-move-ready"]');
    expect(move.text()).toBe("Move on to Plan");
    await move.trigger("click");
    await flushPromises();

    const [path, init] = apiFetchMock.mock.calls[0] as [string, RequestInit];
    expect(path).toBe("/api/workflows/runs/run-1/move-on");
    expect(JSON.parse(init.body as string)).toEqual({ outcome: null, note: "Keep the status-bar shortcuts in the sheet." });
  });

  it("offers one button per outcome, and says how many times Changes has been used", async () => {
    useWorkflowsStore().upsert(reviewRun(0));
    apiFetchMock.mockImplementation(() => respond(reviewRun(0)));
    const wrapper = mount(WorkflowFinishBar, { props: { sessionId: "s5" } });

    expect(wrapper.text()).toContain("You finish Review");
    expect(wrapper.text()).toContain("Pick the outcome. The run goes where it leads.");
    expect(wrapper.text()).toContain("This step declares no files.");
    expect(wrapper.get('[data-testid="workflow-move-pass"]').text()).toBe("Pass: on to Open the pull request");
    expect(wrapper.get('[data-testid="workflow-move-changes"]').text()).toBe("Changes: back to Implement (1 of 2)");

    await wrapper.get('[data-testid="workflow-move-changes"]').trigger("click");
    await flushPromises();
    expect(JSON.parse((apiFetchMock.mock.calls[0] as [string, RequestInit])[1].body as string)).toEqual({ outcome: "changes", note: null });
  });

  it("says why Changes is off once the loop's maximum is used, keeps Pass, and offers to end the run", async () => {
    useWorkflowsStore().upsert(reviewRun(2));
    apiFetchMock.mockImplementation(() => respond({ ...reviewRun(2), status: "ended", withYou: null, updatedAt: "2026-09-23T10:09:00Z" }));
    const wrapper = mount(WorkflowFinishBar, { props: { sessionId: "s5" } });

    const changes = wrapper.get('[data-testid="workflow-move-changes"]');
    expect(changes.attributes("disabled")).toBeDefined();
    expect(changes.text()).toBe("Changes: back to Implement (2 of 2 used)");
    expect(wrapper.get('[data-testid="workflow-move-pass"]').attributes("disabled")).toBeUndefined();
    expect(wrapper.get('[data-testid="workflow-finish-blocked"]').text().replace(/\s+/g, " "))
      .toContain("Review has sent the work back twice, the most this run allows. Pick another outcome, or end the run");

    await wrapper.get('[data-testid="workflow-finish-end"]').trigger("click");
    await flushPromises();
    expect((apiFetchMock.mock.calls[0] as [string])[0]).toBe("/api/workflows/runs/run-1/end");
  });

  it("says what happens while the agent wraps up", () => {
    const run = buildRun();
    useWorkflowsStore().upsert({ ...run, withYou: { ...run.withYou!, wrappingUp: true, outcome: "ready" } });
    const wrapper = mount(WorkflowFinishBar, { props: { sessionId: "s1" } });

    expect(wrapper.find('[data-testid="workflow-finish-bar"]').exists()).toBe(false);
    expect(wrapper.get('[data-testid="workflow-wrapping-up"]').text())
      .toBe("Wrapping up Design: the agent is updating its files and writing a summary for Plan. Then Fleet checks the files and starts Plan.");
  });

  it("shows nothing in a step the agent finishes, or in another session", () => {
    useWorkflowsStore().upsert(buildRun({ withYou: null }));
    expect(mount(WorkflowFinishBar, { props: { sessionId: "s1" } }).html()).not.toContain("wf-finish");

    useWorkflowsStore().upsert(buildRun({ updatedAt: "2026-09-23T10:07:00Z" }));
    expect(mount(WorkflowFinishBar, { props: { sessionId: "other" } }).html()).not.toContain("wf-finish");
  });
});
