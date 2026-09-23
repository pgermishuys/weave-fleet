/**
 * Workflows: Fleet runs a short list of steps, one session per agent step, and moves between them when a step's agent
 * calls `fleet_step_done`. A "You decide" step, a step that stopped without an outcome, and a loop past its maximum
 * stop the run on the user. These are the server's shapes (`/api/workflows`) and the helpers the pages share.
 */

/** The user preference behind the switch in Settings → Workflows. */
export const WORKFLOWS_PREFERENCE_KEY = "Workflows";

/** The user preference that maps each role to a model, per harness. Kept in Fleet, not in the repo. */
export const MODEL_ROLES_PREFERENCE_KEY = "WorkflowModelRoles";

/** The event the server pushes on the "sessions" topic when a run changes. */
export const WORKFLOW_RUN_EVENT = "workflow_run";

/** The tool a step's agent calls to finish the step. */
export const STEP_TOOL = "fleet_step_done";

export type WorkflowRole = "strong" | "standard" | "fast";

export const WORKFLOW_ROLES: readonly WorkflowRole[] = ["strong", "standard", "fast"];

export const ROLE_NAMES: Record<WorkflowRole, string> = {
  strong: "Strong",
  standard: "Standard",
  fast: "Fast",
};

export const ROLE_DESCRIPTIONS: Record<WorkflowRole, string> = {
  strong: "Judgement: deciding what to build and whether it's right",
  standard: "Doing the work: writing and running code",
  fast: "Reading and sorting: short tasks with a clear answer",
};

export function isRole(model: string | null | undefined): model is WorkflowRole {
  return model === "strong" || model === "standard" || model === "fast";
}

/** A model and effort for a role or a step. A null model is the composer's default model. */
export interface WorkflowModelChoice {
  model: string | null;
  effort?: string | null;
}

/** Harness type → role → model. */
export type ModelRoles = Record<string, Partial<Record<WorkflowRole, WorkflowModelChoice>>>;

export function parseModelRoles(value: string | undefined | null): ModelRoles {
  if (!value) return {};
  try {
    const parsed: unknown = JSON.parse(value);
    return parsed && typeof parsed === "object" && !Array.isArray(parsed) ? (parsed as ModelRoles) : {};
  } catch {
    return {};
  }
}

export interface WorkflowChoice {
  label: string;
  to: string;
  note: boolean;
}

export interface WorkflowStep {
  id: string;
  title: string;
  kind: "agent" | "you";
  agent: string | null;
  /** A role, or an exact `provider/model`. */
  model: string | null;
  effort: string | null;
  skill: string | null;
  optional: boolean;
  optionalHint: string | null;
  outcomes: string[];
  routes: Record<string, string>;
  maxLoops: number | null;
  ask: string | null;
  choices: WorkflowChoice[];
  /** `finish: you`: the user moves the step on, not the agent. */
  finishYou: boolean;
  /** `finish: agent`: the agent moves the step on, even with Check with me on. */
  finishAgent: boolean;
  /** The files the step declares, with their variables, e.g. `docs/design/{{slug}}.md`. */
  writes: string[];
  /** An agent step's instructions; null for a You step. */
  prompt?: string | null;
}

export interface Workflow {
  id: string;
  builtIn: boolean;
  /** The repo-relative file; null for a built-in. */
  file: string | null;
  name: string;
  description: string | null;
  placeholder: string | null;
  startsFrom: string | null;
  runsIn: string | null;
  steps: WorkflowStep[];
  /** Each names the file and line. A workflow with errors can't run. */
  errors: string[];
}

export interface WorkflowLibrary {
  repository: string | null;
  repositoryName: string | null;
  workflows: Workflow[];
}

export type RunStatus = "running" | "waiting" | "done" | "ended" | "failed";
export type StepState = "pending" | "running" | "waiting" | "done" | "skipped";

export interface WorkflowRunStep {
  id: string;
  title: string;
  kind: "agent" | "you";
  state: StepState;
  visits: number;
  optional: boolean;
  enabled: boolean;
  outcome: string | null;
  sessionId: string | null;
  model: string | null;
  role: string | null;
  skill: string | null;
  maxLoops: number | null;
  /** The workflow file says `finish: you`. */
  finishYou: boolean;
  /** The workflow file says `finish: agent`: Check with me doesn't make it the user's. */
  finishAgent: boolean;
  /** The user finishes it: how its last visit started, or, for a step still to come, what it will be now. */
  withYou: boolean;
  outcomes: string[];
}

export interface WorkflowRunSession {
  sessionId: string;
  stepId: string;
  visit: number;
  outcome: string | null;
  summary: string | null;
  note: string | null;
  /** The user finishes this visit. */
  withYou: boolean;
  /** The files the step declares, relative to the run's worktree. */
  files: string[];
  /** They were all there before the next step started. */
  filesChecked: boolean;
  /** The short SHA of the commit Fleet made of them then; null when there was nothing to commit. */
  filesCommit: string | null;
  /** Why Fleet couldn't commit them. The run went on. */
  filesCommitError: string | null;
  /** The prompt the session started with. */
  promptMessageId: string | null;
  /** The one prompt Fleet sent when the user pressed Move on. */
  wrapUpMessageId: string | null;
  /** The user's note for the next step. */
  handOffNote: string | null;
}

export interface WorkflowRunChoice {
  id: string;
  label: string;
  note: boolean;
  to: string | null;
  /** It goes on to two or more agent steps, so the card offers it two ways: let it run, or check with me. */
  involvement?: boolean;
}

export type WaitingKind = "you" | "no-outcome" | "loop-limit" | "start-failed" | "missing-files" | "wrap-up-failed" | "skill-off";

/** The choice that goes on past a missing file or a wrap-up that didn't finish. */
export const MOVE_ON_ANYWAY = "move-on-anyway";

/** The choice that starts a step whose skill is off without it, for the rest of the run. */
export const WITHOUT_SKILL = "without-skill";

/** A way to move on from a step the user finishes. */
export interface WorkflowRunMove {
  outcome: string;
  /** Where it leads, past optional steps that are off; null for the end of the run. */
  to: string | null;
  toTitle: string | null;
  /** It sends the work back to an earlier step. */
  back: boolean;
  loopsUsed: number | null;
  maxLoops: number | null;
  /** False when the loop's maximum is used up. */
  allowed: boolean;
}

/** The step the user finishes that's open now. */
export interface WorkflowRunWithYou {
  stepId: string;
  stepTitle: string;
  sessionId: string;
  files: string[];
  /** The user moved on; the agent is bringing the files up to date and writing the summary. */
  wrappingUp: boolean;
  outcome: string | null;
  moves: WorkflowRunMove[];
}

export interface WorkflowRunWaiting {
  kind: WaitingKind;
  stepId: string;
  stepTitle: string;
  message: string;
  question: string | null;
  /** The session the card shows in. */
  sessionId: string | null;
  choices: WorkflowRunChoice[];
  /** For a You step, the files the step before it declares (they open next to the card); for missing files, the step's. */
  files?: string[];
  /** What "let it run" runs: the agent steps before the next You step. */
  thenAlone?: string[];
  nextYouTitle?: string | null;
}

export interface WorkflowRun {
  id: string;
  workflowId: string;
  workflowName: string;
  title: string;
  request: string;
  status: RunStatus;
  currentStepId: string | null;
  result: string | null;
  repositoryPath: string;
  branch: string | null;
  baseBranch: string | null;
  worktreePath: string | null;
  harnessType: string;
  createdAt: string;
  updatedAt: string;
  endedAt: string | null;
  steps: WorkflowRunStep[];
  sessions: WorkflowRunSession[];
  waiting: WorkflowRunWaiting | null;
  /** "Check with me after each step": agent steps that start from now on are ones the user finishes. */
  checkWithMe?: boolean;
  withYou?: WorkflowRunWithYou | null;
}

export function isWorkflowRun(value: unknown): value is WorkflowRun {
  if (!value || typeof value !== "object") return false;
  const run = value as Partial<WorkflowRun>;
  return typeof run.id === "string" && typeof run.status === "string" && Array.isArray(run.steps) && Array.isArray(run.sessions);
}

/** What the Sessions list says about a run, next to its title. A step the user finishes is With you, not Needs you. */
export function runStatusLabel(run: WorkflowRun): { label: string; tone: "wait" | "run" | "with" | "done" | "fail" } {
  switch (run.status) {
    case "waiting":
      return { label: "Needs you", tone: "wait" };
    case "running":
      return run.withYou ? { label: "With you", tone: "with" } : { label: "Working", tone: "run" };
    case "failed":
      return { label: run.result ?? "Failed", tone: "fail" };
    case "ended":
      return { label: "Ended", tone: "done" };
    default:
      return { label: run.result ?? "Done", tone: "done" };
  }
}

/** A step's name in a run, with its pass when it ran more than once ("Implement · 2nd pass"). */
export function stepLabel(run: WorkflowRun, sessionId: string): string | null {
  const session = run.sessions.find((s) => s.sessionId === sessionId);
  if (!session) return null;
  const title = run.steps.find((s) => s.id === session.stepId)?.title ?? session.stepId;
  return session.visit > 1 ? `${title} · ${ordinal(session.visit)} pass` : title;
}

export function ordinal(n: number): string {
  const tens = n % 100;
  if (tens >= 11 && tens <= 13) return `${n}th`;
  return `${n}${["th", "st", "nd", "rd"][n % 10] ?? "th"}`;
}

/** The step number of a session's step among the steps that run in this run ("step 2 of 6"). */
export function stepPosition(run: WorkflowRun, sessionId: string): { index: number; total: number } | null {
  const session = run.sessions.find((s) => s.sessionId === sessionId);
  if (!session) return null;
  const running = run.steps.filter((s) => s.enabled);
  const index = running.findIndex((s) => s.id === session.stepId);
  return index < 0 ? null : { index: index + 1, total: running.length };
}

/** "Opus 5.5" from "github-copilot/claude-opus-5.5" when the catalog has no name for it. */
export function modelShortName(model: string | null): string {
  if (!model) return "Default model";
  const slash = model.indexOf("/");
  return slash >= 0 ? model.slice(slash + 1) : model;
}

/** The skills the enabled steps of a workflow use, in step order, for a run with these optional steps on. */
export function skillsInUse(workflow: Workflow, optionalOn: ReadonlySet<string>): { step: WorkflowStep; skill: string }[] {
  return workflow.steps
    .filter((step) => step.kind === "agent" && step.skill && (!step.optional || optionalOn.has(step.id)))
    .map((step) => ({ step, skill: step.skill! }));
}

/** The loops of a workflow as the steps strip says them: "Review → Implement on changes, at most 2×". */
export function loopNotes(workflow: Workflow): string[] {
  const index = new Map(workflow.steps.map((step, i) => [step.id, i] as const));
  const notes: string[] = [];
  for (const step of workflow.steps) {
    for (const [outcome, target] of Object.entries(step.routes)) {
      const to = index.get(target);
      if (to === undefined || to > (index.get(step.id) ?? 0) || step.maxLoops === null) continue;
      notes.push(`${step.title} → ${workflow.steps[to].title} on ${outcome}, at most ${step.maxLoops}×`);
    }
  }
  return notes;
}

/** A row of a project's session list: a session, or a workflow run with its step sessions under it. */
export type SessionListEntry<T> =
  | { kind: "session"; session: T }
  | { kind: "run"; runId: string; run: WorkflowRun | null; steps: { session: T; label: string }[] };

/**
 * Groups a run's step sessions under one row, where the run's newest session would have been, with its steps in the
 * order they ran. Sessions of a run the store doesn't know yet still group, under a plain header.
 */
export function groupRunSessions<T extends { session: { id: string; title?: string | null; time?: { created?: unknown } }; workflowRunId?: string | null }>(
  sessions: readonly T[],
  runs: (sessionId: string) => WorkflowRun | null,
): SessionListEntry<T>[] {
  const entries: SessionListEntry<T>[] = [];
  const grouped = new Set<string>();
  for (const session of sessions) {
    const runId = session.workflowRunId ?? runs(session.session.id)?.id ?? null;
    if (!runId) {
      entries.push({ kind: "session", session });
      continue;
    }
    if (grouped.has(runId)) continue;
    grouped.add(runId);

    const run = runs(session.session.id);
    const members = sessions.filter((s) => (s.workflowRunId ?? runs(s.session.id)?.id ?? null) === runId);
    const order = (s: T) => {
      const index = run?.sessions.findIndex((r) => r.sessionId === s.session.id) ?? -1;
      return index < 0 ? Number.MAX_SAFE_INTEGER : index;
    };
    const steps = [...members]
      .sort((a, b) => order(a) - order(b))
      .map((s) => ({ session: s, label: (run && stepLabel(run, s.session.id)) ?? stepTitleFromSessionTitle(s.session.title) }));
    entries.push({ kind: "run", runId, run, steps });
  }
  return entries;
}

/** "Plan" from a step session's title, "<run title> · Plan", when the run isn't loaded. */
function stepTitleFromSessionTitle(title: string | null | undefined): string {
  const text = title ?? "";
  const at = text.lastIndexOf(" · ");
  return at >= 0 ? text.slice(at + 3) : text || "Step";
}

/** How the footer Fleet adds to a step's prompt starts; it marks the prompt a step started with. */
export const STEP_FOOTER_START = "This is one step of a Fleet workflow.";

export function isStepPrompt(text: string): boolean {
  return text.includes(STEP_FOOTER_START);
}

/**
 * What a user-side message in a step session is, when Fleet sent it: "Workflow · step 1 of 7 · you finish this step"
 * on the prompt the step started with, "Fleet · you pressed Move on" on the wrap-up. A step the user finishes has no
 * footer, so its prompt is found by its id.
 */
export function workflowMessageLabel(run: WorkflowRun | null, sessionId: string, messageId: string, text: string): string | null {
  if (!run) return null;
  const session = run.sessions.find((s) => s.sessionId === sessionId);
  if (!session) return null;
  if (session.wrapUpMessageId && session.wrapUpMessageId === messageId) return "Fleet · you pressed Move on";
  if (session.promptMessageId === messageId || isStepPrompt(text)) {
    const position = stepPosition(run, sessionId);
    if (!position) return null;
    return `Workflow · step ${position.index} of ${position.total}${session.withYou ? " · you finish this step" : ""}`;
  }
  return null;
}

/** What {@link workflowMessageLabel} reads from a run for one session, as a key that changes only when a label would. */
export function workflowMessageKey(run: WorkflowRun | null, sessionId: string): string | null {
  const session = run?.sessions.find((s) => s.sessionId === sessionId);
  if (!run || !session) return null;
  const position = stepPosition(run, sessionId);
  return `${position?.index}/${position?.total}|${session.promptMessageId}|${session.wrapUpMessageId}|${session.withYou}`;
}

/** The last part of a path: "sheet.md" from "docs/design/sheet.md". */
export function fileName(path: string): string {
  const slash = path.lastIndexOf("/");
  return slash >= 0 ? path.slice(slash + 1) : path;
}

/** "a.md", "a.md and b.html", "a.md, b.html and c.css". */
export function listFiles(files: readonly string[]): string {
  if (files.length <= 1) return files[0] ?? "";
  return `${files.slice(0, -1).join(", ")} and ${files[files.length - 1]}`;
}

/** What a way to move on says: "Move on to Plan", "Pass: on to Open the pull request", "Changes: back to Implement (1 of 2)". */
export function moveLabel(move: WorkflowRunMove, single: boolean): string {
  const where = move.toTitle ?? "the end of the run";
  if (single) return move.toTitle ? `Move on to ${move.toTitle}` : "Finish the run";
  const outcome = move.outcome.charAt(0).toUpperCase() + move.outcome.slice(1);
  if (!move.back) return `${outcome}: on to ${where}`;
  const count = move.maxLoops !== null && move.loopsUsed !== null
    ? move.allowed ? ` (${move.loopsUsed + 1} of ${move.maxLoops})` : ` (${move.maxLoops} of ${move.maxLoops} used)`
    : "";
  return `${outcome}: back to ${where}${count}`;
}

/** Why a way to move on is off: its loop's maximum is used up. */
export function moveBlockedReason(stepTitle: string, move: WorkflowRunMove): string | null {
  if (move.allowed || move.maxLoops === null) return null;
  return `${stepTitle} has sent the work back ${times(move.maxLoops)}, the most this run allows.`;
}

function times(count: number): string {
  return count === 1 ? "once" : count === 2 ? "twice" : `${count} times`;
}

/** The step the run is on, when it's an agent step that's started and not finished. */
export function runningStep(run: WorkflowRun): WorkflowRunStep | null {
  if (run.status !== "running" && run.status !== "waiting") return null;
  const step = run.steps.find((s) => s.id === run.currentStepId);
  return step && step.kind === "agent" && (step.state === "running" || step.state === "waiting") ? step : null;
}

/** The title of the first enabled step after the one the run is on, for "Check with me is on from Review". */
export function nextStepTitle(run: WorkflowRun): string | null {
  const enabled = run.steps.filter((s) => s.enabled);
  const index = enabled.findIndex((s) => s.id === run.currentStepId);
  return index >= 0 ? enabled[index + 1]?.title ?? null : null;
}

/**
 * The note under the header's switch when it differs from how the running step started: a change applies from the
 * next step, so the running one keeps the way it started.
 */
export function checkWithMeNote(run: WorkflowRun): string | null {
  const step = runningStep(run);
  // A step whose file says who finishes it is that whatever the switch says.
  if (!step || step.finishYou || step.finishAgent || Boolean(run.checkWithMe) === step.withYou) return null;
  const next = nextStepTitle(run) ?? "the next step";
  return run.checkWithMe
    ? `Check with me is on from ${next}. This step started on its own, so it still moves on when the agent reports it's done.`
    : `Check with me is off from ${next}. You still finish this step, because it started with you.`;
}
