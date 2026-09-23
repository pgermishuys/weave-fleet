import type { WorkflowCheck, WorkflowDraft, WorkflowFile } from "@/lib/workflow-draft";
import type { WorkflowStep } from "@/lib/workflows";

export function agentStep(overrides: Partial<WorkflowStep> & { id: string }): WorkflowStep {
  return {
    title: overrides.id,
    kind: "agent",
    agent: "build",
    model: "standard",
    effort: null,
    skill: null,
    optional: false,
    optionalHint: null,
    outcomes: ["done"],
    routes: {},
    maxLoops: null,
    ask: null,
    choices: [],
    finishYou: false,
    finishAgent: false,
    writes: [],
    prompt: "The request: {{request}}\n",
    ...overrides,
  };
}

export function youStep(overrides: Partial<WorkflowStep> & { id: string }): WorkflowStep {
  return {
    ...agentStep({ id: overrides.id }),
    kind: "you",
    agent: null,
    model: null,
    outcomes: [],
    prompt: null,
    ask: "Carry on?",
    choices: [{ label: "Carry on", to: "end", note: false }],
    ...overrides,
  };
}

/** Plan → Approve → Implement → Review, with Review's changes going back to Implement. */
export function reviewDraft(): WorkflowDraft {
  return {
    name: "Build it our way",
    description: null,
    placeholder: null,
    steps: [
      agentStep({ id: "plan", title: "Plan", model: "strong", outcomes: ["ready"], writes: [".weave/plans/{{slug}}.md"] }),
      youStep({ id: "ok-plan", title: "Approve the plan", ask: "Build it this way?", choices: [{ label: "Approve", to: "implement", note: false }, { label: "Send back", to: "plan", note: true }] }),
      agentStep({ id: "implement", title: "Implement", prompt: "Build {{steps.plan.files}}.\nThe request: {{request}}\n" }),
      agentStep({ id: "review", title: "Review", model: "strong", skill: "fleet-code-review", outcomes: ["pass", "changes"], routes: { changes: "implement" }, maxLoops: 2, prompt: "Review against {{ steps.plan.files }} and {{steps.plan.summary}}.\n" }),
    ],
  };
}

export function checkOf(draft: WorkflowDraft | null, overrides: Partial<WorkflowCheck> = {}): WorkflowCheck {
  return { text: "name: Build it our way\n", errors: [], draft, comments: [], ...overrides };
}

export function fileOf(check: WorkflowCheck, overrides: Partial<WorkflowFile> = {}): WorkflowFile {
  return { workflowId: "repo:build-it-our-way", file: ".weave/workflows/build-it-our-way.yaml", hash: "h1", check, ...overrides };
}

export function respond(body: unknown, status = 200): Promise<Response> {
  return Promise.resolve(new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } }));
}
