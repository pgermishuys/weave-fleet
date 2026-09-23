import { describe, expect, it } from "vitest";
import {
  groupRunSessions,
  isStepPrompt,
  loopNotes,
  runStatusLabel,
  skillsInUse,
  stepLabel,
  stepPosition,
  type Workflow,
  type WorkflowRun,
} from "@/lib/workflows";

function run(overrides: Partial<WorkflowRun> = {}): WorkflowRun {
  return {
    id: "run-1",
    workflowId: "builtin:build-a-feature",
    workflowName: "Build a feature",
    title: "Keyboard shortcut sheet",
    request: "Press ? to see every keyboard shortcut",
    status: "running",
    currentStepId: "implement",
    result: null,
    repositoryPath: "/repo",
    branch: "fleet/keyboard-shortcut-sheet",
    baseBranch: null,
    worktreePath: "/repo-worktrees/keyboard-shortcut-sheet",
    harnessType: "opencode",
    createdAt: "2026-09-23T10:00:00Z",
    updatedAt: "2026-09-23T10:10:00Z",
    endedAt: null,
    steps: [
      { id: "design", title: "Design", kind: "agent", state: "skipped", visits: 0, optional: true, enabled: false, outcome: null, sessionId: null, model: null, role: "strong", skill: "fleet-mockups", maxLoops: null },
      { id: "plan", title: "Plan", kind: "agent", state: "done", visits: 2, optional: false, enabled: true, outcome: "ready", sessionId: "s2", model: null, role: "strong", skill: null, maxLoops: null },
      { id: "ok-plan", title: "Approve the plan", kind: "you", state: "done", visits: 1, optional: false, enabled: true, outcome: "Approve", sessionId: null, model: null, role: null, skill: null, maxLoops: null },
      { id: "implement", title: "Implement", kind: "agent", state: "running", visits: 1, optional: false, enabled: true, outcome: null, sessionId: "s3", model: null, role: "standard", skill: null, maxLoops: null },
    ],
    sessions: [
      { sessionId: "s1", stepId: "plan", visit: 1, outcome: "ready", summary: "v1", note: null },
      { sessionId: "s2", stepId: "plan", visit: 2, outcome: "ready", summary: "v2", note: "Leave the status bar alone." },
      { sessionId: "s3", stepId: "implement", visit: 1, outcome: null, summary: null, note: null },
    ],
    waiting: null,
    ...overrides,
  };
}

const item = (id: string, workflowRunId: string | null = null, title = id) => ({ session: { id, title }, workflowRunId });

describe("workflows", () => {
  it("names a step session by its step, with the pass when it ran again", () => {
    expect(stepLabel(run(), "s1")).toBe("Plan");
    expect(stepLabel(run(), "s2")).toBe("Plan · 2nd pass");
    expect(stepLabel(run(), "s3")).toBe("Implement");
  });

  it("counts a step among the steps that run in this run", () => {
    // Design is off, so Implement is the third of three.
    expect(stepPosition(run(), "s3")).toEqual({ index: 3, total: 3 });
  });

  it("groups a run's sessions under one row where its newest session was, in the order they ran", () => {
    const current = run();
    const sessions = [item("a"), item("s3", "run-1"), item("b"), item("s1", "run-1"), item("s2", "run-1")];

    const entries = groupRunSessions(sessions, (id) => (current.sessions.some((s) => s.sessionId === id) ? current : null));

    expect(entries.map((e) => (e.kind === "run" ? `run:${e.runId}` : e.session.session.id))).toEqual(["a", "run:run-1", "b"]);
    const group = entries[1];
    expect(group.kind === "run" && group.steps.map((s) => s.label)).toEqual(["Plan", "Plan · 2nd pass", "Implement"]);
  });

  it("groups a run the store doesn't know yet by the sessions' run id and titles", () => {
    const entries = groupRunSessions([item("x", "run-9", "Shortcut sheet · Plan")], () => null);

    expect(entries).toHaveLength(1);
    expect(entries[0].kind === "run" && entries[0].steps[0].label).toBe("Plan");
  });

  it("says what a run is doing", () => {
    expect(runStatusLabel(run({ status: "waiting" }))).toEqual({ label: "Needs you", tone: "wait" });
    expect(runStatusLabel(run({ status: "done", result: "PR #12 opened" }))).toEqual({ label: "PR #12 opened", tone: "done" });
  });

  it("lists loops and the skills the enabled steps use", () => {
    const workflow = {
      steps: [
        { id: "design", title: "Design", kind: "agent", optional: true, skill: "fleet-mockups", routes: {}, maxLoops: null },
        { id: "implement", title: "Implement", kind: "agent", optional: false, skill: null, routes: {}, maxLoops: null },
        { id: "review", title: "Review", kind: "agent", optional: false, skill: "fleet-code-review", routes: { changes: "implement" }, maxLoops: 2 },
      ],
    } as unknown as Workflow;

    expect(loopNotes(workflow)).toEqual(["Review → Implement on changes, at most 2×"]);
    expect(skillsInUse(workflow, new Set()).map((s) => s.skill)).toEqual(["fleet-code-review"]);
    expect(skillsInUse(workflow, new Set(["design"])).map((s) => s.skill)).toEqual(["fleet-mockups", "fleet-code-review"]);
  });

  it("knows the prompt a step started with by Fleet's footer", () => {
    expect(isStepPrompt("Plan it.\n\nThis is one step of a Fleet workflow. When the step is finished, call fleet_step_done once…")).toBe(true);
    expect(isStepPrompt("Plan it.")).toBe(false);
  });
});
