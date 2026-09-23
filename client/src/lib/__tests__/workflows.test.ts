import { describe, expect, it } from "vitest";
import {
  checkWithMeNote,
  groupRunSessions,
  moveBlockedReason,
  moveLabel,
  workflowMessageLabel,
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
      { id: "design", title: "Design", kind: "agent", state: "skipped", visits: 0, optional: true, enabled: false, outcome: null, sessionId: null, model: null, role: "strong", skill: "fleet-mockups", maxLoops: null, finishYou: false, finishAgent: false, withYou: false, outcomes: [] },
      { id: "plan", title: "Plan", kind: "agent", state: "done", visits: 2, optional: false, enabled: true, outcome: "ready", sessionId: "s2", model: null, role: "strong", skill: null, maxLoops: null, finishYou: false, finishAgent: false, withYou: false, outcomes: [] },
      { id: "ok-plan", title: "Approve the plan", kind: "you", state: "done", visits: 1, optional: false, enabled: true, outcome: "Approve", sessionId: null, model: null, role: null, skill: null, maxLoops: null, finishYou: false, finishAgent: false, withYou: false, outcomes: [] },
      { id: "implement", title: "Implement", kind: "agent", state: "running", visits: 1, optional: false, enabled: true, outcome: null, sessionId: "s3", model: null, role: "standard", skill: null, maxLoops: null, finishYou: false, finishAgent: false, withYou: false, outcomes: [] },
    ],
    sessions: [
      { sessionId: "s1", stepId: "plan", visit: 1, outcome: "ready", summary: "v1", note: null, withYou: false, files: [], filesChecked: false, filesCommit: null, filesCommitError: null, promptMessageId: null, wrapUpMessageId: null, handOffNote: null },
      { sessionId: "s2", stepId: "plan", visit: 2, outcome: "ready", summary: "v2", note: "Leave the status bar alone.", withYou: false, files: [], filesChecked: false, filesCommit: null, filesCommitError: null, promptMessageId: null, wrapUpMessageId: null, handOffNote: null },
      { sessionId: "s3", stepId: "implement", visit: 1, outcome: null, summary: null, note: null, withYou: false, files: [], filesChecked: false, filesCommit: null, filesCommitError: null, promptMessageId: null, wrapUpMessageId: null, handOffNote: null },
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
        { id: "design", title: "Design", kind: "agent", optional: true, skill: "fleet-mockups", routes: {}, maxLoops: null, finishYou: false, finishAgent: false, withYou: false, outcomes: [] },
        { id: "implement", title: "Implement", kind: "agent", optional: false, skill: null, routes: {}, maxLoops: null, finishYou: false, finishAgent: false, withYou: false, outcomes: [] },
        { id: "review", title: "Review", kind: "agent", optional: false, skill: "fleet-code-review", routes: { changes: "implement" }, maxLoops: 2, finishYou: false, finishAgent: false, withYou: false, outcomes: [] },
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

  it("says a run with a step the user finishes open is With you, not Needs you", () => {
    const move = { outcome: "ready", to: "plan", toTitle: "Plan", back: false, loopsUsed: null, maxLoops: null, allowed: true };
    const withYou = { stepId: "implement", stepTitle: "Implement", sessionId: "s3", files: [], wrappingUp: false, outcome: null, moves: [move] };
    expect(runStatusLabel(run({ withYou }))).toEqual({ label: "With you", tone: "with" });
    expect(runStatusLabel(run({ withYou: null }))).toEqual({ label: "Working", tone: "run" });
  });

  it("names the ways to move on the way the bar shows them", () => {
    const on = { outcome: "pass", to: "ok-pr", toTitle: "Open the pull request", back: false, loopsUsed: null, maxLoops: null, allowed: true };
    const back = { outcome: "changes", to: "implement", toTitle: "Implement", back: true, loopsUsed: 0, maxLoops: 2, allowed: true };
    expect(moveLabel({ ...on, outcome: "ready", toTitle: "Plan" }, true)).toBe("Move on to Plan");
    expect(moveLabel(on, false)).toBe("Pass: on to Open the pull request");
    expect(moveLabel(back, false)).toBe("Changes: back to Implement (1 of 2)");
    expect(moveLabel({ ...back, loopsUsed: 1 }, false)).toBe("Changes: back to Implement (2 of 2)");
    expect(moveLabel({ ...back, loopsUsed: 2, allowed: false }, false)).toBe("Changes: back to Implement (2 of 2 used)");
    expect(moveBlockedReason("Review", { ...back, loopsUsed: 2, allowed: false })).toBe("Review has sent the work back twice, the most this run allows.");
    expect(moveBlockedReason("Review", back)).toBeNull();
  });

  it("marks what Fleet sent into a step session: the first prompt by its id, and the wrap-up", () => {
    const current = run({
      sessions: [{
        sessionId: "s3", stepId: "implement", visit: 1, outcome: null, summary: null, note: null, withYou: true,
        files: [], filesChecked: false, filesCommit: null, filesCommitError: null, promptMessageId: "msg_first", wrapUpMessageId: "msg_wrap", handOffNote: null,
      }],
    });
    expect(workflowMessageLabel(current, "s3", "msg_first", "Build the plan.")).toBe("Workflow · step 3 of 3 · you finish this step");
    expect(workflowMessageLabel(current, "s3", "msg_wrap", "The user is moving on to Review.")).toBe("Fleet · you pressed Move on");
    expect(workflowMessageLabel(current, "s3", "msg_user", "Make the status bar read from the same list.")).toBeNull();
    expect(workflowMessageLabel(null, "s3", "msg_first", "")).toBeNull();
  });

  it("says Check with me changes only the steps after the running one", () => {
    const running = run({ checkWithMe: true });
    expect(checkWithMeNote(running)).toBe("Check with me is on from the next step. This step started on its own, so it still moves on when the agent reports it's done.");
    expect(checkWithMeNote(run({ checkWithMe: false }))).toBeNull();

    // Design says finish: you, so the switch has nothing to say about it.
    const design = run({ checkWithMe: false });
    design.steps = design.steps.map((s) => (s.id === "implement" ? { ...s, finishYou: true, withYou: true } : s));
    expect(checkWithMeNote(design)).toBeNull();

    // The push says finish: agent, so it runs on its own with Check with me on, and that's not news.
    const push = run({ checkWithMe: true });
    push.steps = push.steps.map((s) => (s.id === "implement" ? { ...s, finishAgent: true, withYou: false } : s));
    expect(checkWithMeNote(push)).toBeNull();
  });
});
