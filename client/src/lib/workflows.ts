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
}

export interface WorkflowRunSession {
  sessionId: string;
  stepId: string;
  visit: number;
  outcome: string | null;
  summary: string | null;
  note: string | null;
}

export interface WorkflowRunChoice {
  id: string;
  label: string;
  note: boolean;
  to: string | null;
}

export type WaitingKind = "you" | "no-outcome" | "loop-limit" | "start-failed";

export interface WorkflowRunWaiting {
  kind: WaitingKind;
  stepId: string;
  stepTitle: string;
  message: string;
  question: string | null;
  /** The session the card shows in. */
  sessionId: string | null;
  choices: WorkflowRunChoice[];
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
}

export function isWorkflowRun(value: unknown): value is WorkflowRun {
  if (!value || typeof value !== "object") return false;
  const run = value as Partial<WorkflowRun>;
  return typeof run.id === "string" && typeof run.status === "string" && Array.isArray(run.steps) && Array.isArray(run.sessions);
}

/** What the Sessions list says about a run, next to its title. */
export function runStatusLabel(run: WorkflowRun): { label: string; tone: "wait" | "run" | "done" | "fail" } {
  switch (run.status) {
    case "waiting":
      return { label: "Needs you", tone: "wait" };
    case "running":
      return { label: "Working", tone: "run" };
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
