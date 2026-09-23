import type { WorkflowRun, WorkflowRunSession, WorkflowRunStep } from "@/lib/workflows";

/** A step of a run as the server sends it, with only what a test cares about set. */
export function runStep(step: Partial<WorkflowRunStep> & Pick<WorkflowRunStep, "id" | "title">): WorkflowRunStep {
  return {
    kind: "agent",
    state: "pending",
    visits: 0,
    optional: false,
    enabled: true,
    outcome: null,
    sessionId: null,
    model: null,
    role: null,
    skill: null,
    maxLoops: null,
    finishYou: false,
    withYou: false,
    outcomes: [],
    ...step,
  };
}

/** A step session of a run. */
export function runSession(session: Partial<WorkflowRunSession> & Pick<WorkflowRunSession, "sessionId" | "stepId">): WorkflowRunSession {
  return {
    visit: 1,
    outcome: null,
    summary: null,
    note: null,
    withYou: false,
    files: [],
    filesChecked: false,
    promptMessageId: null,
    wrapUpMessageId: null,
    handOffNote: null,
    ...session,
  };
}

/** A Build a feature run with Design, Plan, Approve the plan, Implement and Review. */
export function buildRun(overrides: Partial<WorkflowRun> = {}): WorkflowRun {
  return {
    id: "run-1",
    workflowId: "builtin:build-a-feature",
    workflowName: "Build a feature",
    title: "Keyboard shortcut sheet",
    request: "Press ? to see every keyboard shortcut",
    status: "running",
    currentStepId: "design",
    result: null,
    repositoryPath: "/repo",
    branch: "fleet/keyboard-shortcut-sheet",
    baseBranch: "main",
    worktreePath: "/repo-worktrees/keyboard-shortcut-sheet",
    harnessType: "opencode",
    createdAt: "2026-09-23T10:00:00Z",
    updatedAt: "2026-09-23T10:05:00Z",
    endedAt: null,
    steps: [
      runStep({ id: "design", title: "Design", state: "running", visits: 1, sessionId: "s1", finishYou: true, withYou: true, outcomes: ["ready"] }),
      runStep({ id: "plan", title: "Plan", outcomes: ["ready"] }),
      runStep({ id: "ok-plan", title: "Approve the plan", kind: "you" }),
      runStep({ id: "implement", title: "Implement", outcomes: ["done"] }),
      runStep({ id: "review", title: "Review", outcomes: ["pass", "changes"], maxLoops: 2 }),
    ],
    sessions: [runSession({ sessionId: "s1", stepId: "design", withYou: true, files: ["docs/design/sheet.md", "docs/design/sheet.html"], promptMessageId: "msg_1" })],
    waiting: null,
    checkWithMe: false,
    withYou: {
      stepId: "design",
      stepTitle: "Design",
      sessionId: "s1",
      files: ["docs/design/sheet.md", "docs/design/sheet.html"],
      wrappingUp: false,
      outcome: null,
      moves: [{ outcome: "ready", to: "plan", toTitle: "Plan", back: false, loopsUsed: null, maxLoops: null, allowed: true }],
    },
    ...overrides,
  };
}

export function respond(body: unknown): Promise<Response> {
  return Promise.resolve(new Response(JSON.stringify(body), { status: 200, headers: { "Content-Type": "application/json" } }));
}
