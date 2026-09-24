/**
 * The workflow designer's model: a workflow as its file says it, valid or not yet, and the edits the inspector and
 * the canvas make to it. Every edit returns a new draft. Checking and writing the file are the server's: the draft
 * goes to `/api/workflows/check`, which writes it in Fleet's layout and runs the parser runs use.
 */
import type { WorkflowChoice, WorkflowStep } from "@/lib/workflows";

/** Where an outcome or a choice leads when the run is over. */
export const END = "end";

export interface WorkflowDraft {
  name: string;
  description: string | null;
  /** The Run box's hint. */
  placeholder: string | null;
  steps: WorkflowStep[];
}

/** An error the parser found: its line, and the index of the step whose lines hold it. */
export interface WorkflowProblem {
  line: number;
  message: string;
  step: number | null;
}

export interface WorkflowComment {
  line: number;
  text: string;
}

export interface WorkflowCheck {
  /** What Save writes: the File view's text, or the draft written in Fleet's layout. */
  text: string;
  errors: WorkflowProblem[];
  /** What the designer shows; null when it can't show this text without losing some of it. */
  draft: WorkflowDraft | null;
  /** The comments a save from the designer would remove. */
  comments: WorkflowComment[];
}

/** A repository's workflow file, open in the editor. */
export interface WorkflowFile {
  workflowId: string;
  /** The repo-relative path, e.g. `.weave/workflows/deps.yaml`. */
  file: string;
  /** The file as it was read or last saved; a save with an older one is refused. */
  hash: string;
  check: WorkflowCheck;
}

/** What asking the model for a workflow cost: all its tokens, and how many were read from the provider's cache. */
export interface DraftTokens {
  total: number;
  fromCache: number;
}

/** A workflow the model drafted, from a session or a description. Nothing is written until it's saved. */
export interface DraftedWorkflow {
  /** The repository Save creates the file in. */
  repository: string;
  repositoryName: string;
  /** The session it was drafted from; null when it was drafted from a description. */
  sessionTitle: string | null;
  check: WorkflowCheck;
  /** 2 when the first answer had errors and the model was asked again. */
  asks: number;
  /** Null when the harness doesn't report tokens. */
  tokens: DraftTokens | null;
}

/** "Drafted from Fix the login bug. Review it before saving." */
export function draftedFrom(drafted: Pick<DraftedWorkflow, "sessionTitle">): string {
  return drafted.sessionTitle
    ? `Drafted from ${drafted.sessionTitle}. Review it before saving.`
    : "Drafted from your description. Review it before saving.";
}

/** "Asked the model once · 3,412 tokens, 3,100 from the cache." */
export function draftCost(drafted: Pick<DraftedWorkflow, "asks" | "tokens">): string {
  const asked = drafted.asks > 1 ? "Asked the model twice: its first answer had errors" : "Asked the model once";
  if (!drafted.tokens) return `${asked}.`;
  const total = drafted.tokens.total.toLocaleString("en-US");
  const cached = drafted.tokens.fromCache > 0 ? `, ${drafted.tokens.fromCache.toLocaleString("en-US")} from the cache` : "";
  return `${asked} · ${total} tokens${cached}.`;
}

/** What the menu item and the dialog say it costs. */
export const DRAFT_COST_NOTE = "Asks the model once. From a session it reads the conversation from the cache, so it's cheap.";

/** "Tidy up a flaky test" → `tidy-up-a-flaky-test`, as the server names the file. */
export function slugOf(name: string): string {
  const slug = name.toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "").slice(0, 60).replace(/-+$/, "");
  return slug || "workflow";
}

/** The variables Fleet fills in, for the chips under a prompt. Step variables are added per step. */
export const RUN_VARIABLES = ["request", "slug", "previous.summary", "previous.files", "run.branch", "run.base"] as const;

/** The chips under a step's prompt: the run's variables, then the steps before it that hand something on. */
export function variablesFor(draft: WorkflowDraft, index: number): string[] {
  const variables: string[] = [...RUN_VARIABLES];
  for (const step of draft.steps.slice(0, index)) {
    if (step.kind !== "agent") continue;
    variables.push(`steps.${step.id}.summary`);
    if (step.writes.length > 0) variables.push(`steps.${step.id}.files`);
  }
  return variables;
}

/** An id no step has yet: `step`, `step-2`, … */
export function uniqueId(draft: WorkflowDraft, base: string): string {
  const taken = new Set(draft.steps.map((step) => step.id));
  if (!taken.has(base)) return base;
  let n = 2;
  while (taken.has(`${base}-${n}`)) n++;
  return `${base}-${n}`;
}

function blankStep(): Omit<WorkflowStep, "id" | "title" | "kind"> {
  return {
    agent: null,
    model: null,
    effort: null,
    skill: null,
    optional: false,
    optionalHint: null,
    outcomes: [],
    routes: {},
    maxLoops: null,
    ask: null,
    choices: [],
    finishYou: false,
    finishAgent: false,
    writes: [],
    prompt: null,
  };
}

/** What + → Agent step adds: runs on Standard, sees the request and the step before it, and finishes with done. */
export function newAgentStep(draft: WorkflowDraft): WorkflowStep {
  return {
    ...blankStep(),
    id: uniqueId(draft, "step"),
    title: "New step",
    kind: "agent",
    agent: "build",
    model: "standard",
    outcomes: ["done"],
    prompt: "The request: {{request}}\n\n{{previous.summary}}",
  };
}

/** What + → You decide adds before `nextId`: carry on to it, or stop there. */
export function newYouStep(draft: WorkflowDraft, nextId: string | null): WorkflowStep {
  return {
    ...blankStep(),
    id: uniqueId(draft, "ask"),
    title: "Check with you",
    kind: "you",
    ask: "Carry on?",
    choices: [
      { label: "Carry on", to: nextId ?? END, note: false },
      { label: "Stop here", to: END, note: false },
    ],
  };
}

export function insertStep(draft: WorkflowDraft, index: number, step: WorkflowStep): WorkflowDraft {
  const steps = [...draft.steps];
  steps.splice(index, 0, step);
  return { ...draft, steps };
}

export function removeStep(draft: WorkflowDraft, index: number): WorkflowDraft {
  return { ...draft, steps: draft.steps.filter((_, i) => i !== index) };
}

export function updateStep(draft: WorkflowDraft, index: number, patch: Partial<WorkflowStep>): WorkflowDraft {
  return { ...draft, steps: draft.steps.map((step, i) => (i === index ? { ...step, ...patch } : step)) };
}

function escapeRegExp(text: string): string {
  return text.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

/**
 * Changes a step's id, and every reference to it follows: outcomes that lead there, choices that lead there, and
 * `{{steps.<id>.summary}}` and `{{steps.<id>.files}}` in every prompt.
 */
export function renameStepId(draft: WorkflowDraft, from: string, to: string): WorkflowDraft {
  if (from === to) return draft;
  const variable = new RegExp(`\\{\\{(\\s*)steps\\.${escapeRegExp(from)}\\.(summary|files)(\\s*)\\}\\}`, "g");
  return {
    ...draft,
    steps: draft.steps.map((step) => ({
      ...step,
      id: step.id === from ? to : step.id,
      routes: Object.fromEntries(Object.entries(step.routes).map(([outcome, target]) => [outcome, target === from ? to : target])),
      choices: step.choices.map((choice) => (choice.to === from ? { ...choice, to } : choice)),
      prompt: step.prompt ? step.prompt.replace(variable, `{{$1steps.${to}.$2$3}}`) : step.prompt,
    })),
  };
}

/** Renames an outcome; where it led goes with it. */
export function renameOutcome(step: WorkflowStep, from: string, to: string): Partial<WorkflowStep> {
  const routes: Record<string, string> = {};
  for (const [outcome, target] of Object.entries(step.routes)) routes[outcome === from ? to : outcome] = target;
  return { outcomes: step.outcomes.map((o) => (o === from ? to : o)), routes };
}

export function removeOutcome(step: WorkflowStep, outcome: string): Partial<WorkflowStep> {
  const routes = { ...step.routes };
  delete routes[outcome];
  return { outcomes: step.outcomes.filter((o) => o !== outcome), routes };
}

/** An outcome that isn't used yet: `done`, `outcome-2`, … */
export function newOutcome(step: WorkflowStep): string {
  if (!step.outcomes.includes("done")) return "done";
  let n = 2;
  while (step.outcomes.includes(`outcome-${n}`)) n++;
  return `outcome-${n}`;
}

/** Where an outcome leads: a step's id, `end`, or null for the next step (which leaves it out of `on:`). */
export function setRoute(step: WorkflowStep, outcome: string, target: string | null): Partial<WorkflowStep> {
  const routes = { ...step.routes };
  if (target === null) delete routes[outcome];
  else routes[outcome] = target;
  return { routes };
}

/** An outcome or choice that goes back to an earlier step, or the same one: a loop, which needs a max. */
export function isLoop(draft: WorkflowDraft, from: number, target: string | null | undefined): boolean {
  if (!target || target === END) return false;
  const to = draft.steps.findIndex((step) => step.id === target);
  return to >= 0 && to <= from;
}

/** The outcomes of a step that are loops. */
export function loopOutcomes(draft: WorkflowDraft, index: number): string[] {
  const step = draft.steps[index];
  if (!step) return [];
  return step.outcomes.filter((outcome) => isLoop(draft, index, step.routes[outcome]));
}

/**
 * A step with no loop left has no max (the parser would say max doesn't apply). A new loop keeps its max empty, so
 * the step says it needs one.
 */
export function dropUnusedMax(draft: WorkflowDraft, index: number): WorkflowDraft {
  const step = draft.steps[index];
  if (!step || step.maxLoops === null || loopOutcomes(draft, index).length > 0) return draft;
  return updateStep(draft, index, { maxLoops: null });
}

/** "Implement", "End the run", or "Next step" for where an outcome or choice leads. */
export function targetTitle(draft: WorkflowDraft, target: string | null | undefined): string {
  if (!target) return "Next step";
  if (target === END) return "End the run";
  return draft.steps.find((step) => step.id === target)?.title || target;
}

export function updateChoice(step: WorkflowStep, index: number, patch: Partial<WorkflowChoice>): Partial<WorkflowStep> {
  return { choices: step.choices.map((choice, i) => (i === index ? { ...choice, ...patch } : choice)) };
}

/** Same workflow? What the editor compares to know there's something to save. */
export function sameDraft(a: WorkflowDraft | null, b: WorkflowDraft | null): boolean {
  return JSON.stringify(a) === JSON.stringify(b);
}

/** A deep copy, so edits never touch what the server sent. */
export function cloneDraft(draft: WorkflowDraft): WorkflowDraft {
  return JSON.parse(JSON.stringify(draft)) as WorkflowDraft;
}
