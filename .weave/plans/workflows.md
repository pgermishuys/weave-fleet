# Workflows: Fleet runs a short list of steps

A workflow is a short list of steps. Fleet runs it, not a coordinator agent: each agent step is an ordinary Fleet
session with its own agent, model and optional skill. Fleet moves the run from step to step, costing no tokens in
between, and stops wherever a step asks the user. Proposal, mockup and test results:
https://claude.ai/artifact/NPf5EP37d7LCicTPgRcgLQ (Library, Designer, a run in Sessions, Settings → Workflows).
Feasibility kit: `~/.cache/fleet-workflows-spike`.

## Decisions (2026-09-23, with the user)

- **Opt-in**, off by default, marked Experimental. With it off, the Workflows rail item, the runner and the step
  tool don't exist, and nothing else in Fleet changes.
- **Fleet runs the workflow.** A step ends when its session calls `fleet_step_done(outcome, summary)`. Fleet never
  advances on idle alone and never re-prompts an agent on its own.
- **YAML files.** Built-ins ship inside Fleet, read-only. Your own live in the repo under `.weave/workflows/`.
- **Own rail item.** Automations may start workflows later (Stage 3); they aren't part of Automations.
- **Build a feature** has one approval, on the plan. Design is an optional step, off by default.
- **Models go through roles** (strong / standard / fast), mapped per harness in Settings → Workflows. A step may pin
  an exact model. A run may override the roles.
- **Hand-off** between steps is the step's full `fleet_step_done` summary, not a 600-character cut.
- **Pooled OpenCode first, harness-neutral always.** OpenCode names stop at the adapters. Both OpenCode harnesses
  are in scope; Claude Code and Pi say workflows aren't available on them.
- **No context injected unasked.** The step footer is the only addition, and only in step sessions.
- **No stop, pause or resume** on sessions. Ending a run only stops Fleet advancing it.
- A waiting approval is **run state**, not a new session activity status (the six status-derivation sites stay
  untouched).

## Stage 1 — the runner and Build a feature (this PR)

### 1. The switch

- Preference `Workflows` (`true`/`false`), config default `Fleet:Harness:Workflows` (false). Copied from
  `SessionMessages`: `WorkflowsFeature.IsEnabledAsync()` reads the preference, then the option.
- Settings gets a **Workflows** section (after Weave). Its first card is the switch, marked Experimental, with the
  mockup's copy. The second card is Model roles (below), shown only while the switch is on.
- Off: no rail item, `/workflows` redirects home, the workflow API answers 404 ("Workflows are turned off in
  Settings."), the harnesses don't get the step tool, the runner doesn't advance runs.
- Harness processes: `FLEET_WORKFLOWS=1` in a process's environment when the owner has the switch on. It's part of
  the pool key (OpenCode) and the server setup (OpenCode 2), exactly as `FLEET_SESSION_MESSAGES` is, so sessions
  started with it on and off never share a process. Normal sessions and step sessions share one process; normal
  ones have the tool denied (section 4).

### 2. Workflow files

Format (documented in `docs/workflows.md`):

```yaml
name: Build a feature
description: Turns a sentence into a reviewed pull request. …
starts-from: sentence          # Stage 1: sentence only
runs-in: new-worktree          # Stage 1: new-worktree only (one worktree and branch per run, from the base branch)
steps:
  - id: design
    title: Design
    agent: build               # optional; the harness's default agent when left out
    model: strong              # strong | standard | fast | an exact provider/model
    effort: high               # optional
    skill: fleet-mockups       # optional
    optional: For UI and new features   # or `true`; off unless switched on for a run
    prompt: |
      Design {{request}}. …
    outcomes: [ready]
  - id: ok-plan
    title: Approve the plan
    you: Build it this way?
    choices:
      Approve: implement
      Send back with a note: { to: plan, note: true }
  - id: review
    …
    outcomes: [pass, changes]
    on: { changes: implement, max: 2 }
```

- `on` maps outcomes to a step id or `end`; an outcome not in `on` goes to the next step in the file. An outcome
  that goes back to an earlier step is a loop and needs `max`: how many times this step may send work back in a
  run. The next time goes to the user (Needs you).
- `choices` map a label to a step id or `end`, or to `{ to, note: true }` when the choice asks for a note. The
  note goes into that step's next prompt.
- Variables: `{{request}}`, `{{slug}}`, `{{previous.summary}}`, `{{run.branch}}`, `{{run.base}}`. Anything else is
  an error when the file is read.
- Validation: unique ids, known targets, at least one outcome per agent step, `max` on every loop, a You step has
  at least one choice, only known keys. An error names the file and line: `.weave/workflows/deps.yaml, line 12:
  "implment" isn't a step in this workflow.` A bad file shows as an error row in the list; the rest still load.
- **Parser:** YamlDotNet's representation model (`YamlStream` → nodes), walked by hand into records. No
  reflection, no serializer, so it's AOT-safe, and nodes carry line numbers for the errors. The CI AOT job calls
  `GET /api/workflows` on the published binary and checks Build a feature comes back.
- Built-ins are embedded resources in `WeaveFleet.Application` (`Workflows/BuiltIn/build-a-feature.yaml`).
- `WorkflowCatalog.ListAsync(repoPath)`: built-ins, then `.weave/workflows/*.yaml` in the repo (sorted by name),
  each with its errors. Ids: `builtin:build-a-feature`, `repo:<file name without extension>`.

### 3. The runner (Application, harness-neutral)

Tables (migration `038_add_workflow_runs.sql`):

- `workflow_runs`: id, user_id, workflow_id, workflow_name, definition (the YAML as it was when the run started,
  so an edited file doesn't change a run in flight), request, slug, title, repository_path, base_branch, branch,
  worktree_path, harness_type, harness_profile_id, options (JSON: optional steps switched on, model overrides,
  resolved model per step), status (`running`, `waiting`, `done`, `ended`, `failed`), current_step_id,
  waiting_reason, created_at, updated_at, ended_at.
- `workflow_run_steps`: id, run_id, step_id, visit (1, 2, … for loops), session_id, status (`running`,
  `done`, `waiting`, `skipped`, `decided`), outcome, summary, note (a note sent back into this visit), prompt
  message id, started_at, finished_at.
- `sessions.workflow_run_id`: the run a step session belongs to. The session list and the harness adapters read it.

`WorkflowRunner`:

- **Start** (`POST /api/workflows/runs`): checks the switch, the harness's capability (below), the workflow's
  errors, the request, and every enabled step's model and skill. A step whose model isn't in the harness's catalog
  stops the start: "Review's model, Opus 5.5 (Anthropic), isn't available on OpenCode. Sign in to Anthropic, or
  pick another model for Strong." A step whose built-in skill is off stops it too, naming Settings → Skills. The
  Run box shows the same thing inline before Run ("Review uses fleet-code-review, which is off. Turn it on in
  Settings → Skills", with a link) and disables Run; it follows the optional-step chips, so Check it runs needs
  fleet-run only while it's on. The server check is the backstop.
- Each agent step starts a **new session** through `SessionOrchestrator.CreateSessionAsync` with
  `WorkflowRunId` set, the step's agent, model and effort, and title `<run title> · <step title>`. The first
  agent step's session makes the run's worktree (repository source, `worktree`, base branch), named by Settings →
  Worktree naming from the run's title like any new worktree; later steps use the same worktree
  (`existingWorktreePath`).
- The prompt is the step's instructions with variables filled in, then the note if the step was sent back
  (`Sent back with a note:\n<note>`), then the footer that worked in the tests: *"This is one step of a Fleet
  workflow. When the step is finished, call fleet_step_done once, as your last action, with outcome set to one
  of: X, Y. Put what the next step needs in summary."* A step with a skill adds "Use the <skill> skill." before it.
- **Done**: `fleet_step_done` from the step's own session records outcome and summary, answers the tool, and
  moves the run on at once (it doesn't wait for idle). An outcome the step doesn't list is refused with the list.
  A second call is refused: "This step is already done."
- **Idle without done**: the relay hands every translated event to `IWorkflowStepObserver`. When a step
  session's turn (one with an assistant message after the step's prompt) ends, or fails, and the step hasn't
  called the tool, the step goes to Needs you: "Implement stopped without finishing the step." The user picks an
  outcome, or replies to the agent in that session; if the agent then calls the tool the run moves on. Fleet
  never prompts it on its own.
- **You decide**: the run stops (`waiting`) with a card in the conversation of the last step's session. Each
  choice leads to a step or ends the run; a note choice puts the note into that step's next prompt.
- **Loops**: an outcome going back counts against the step's `max`. Past it, the run waits for the user: "Review
  asked for changes a third time. At most 2 were allowed." with the outcomes as buttons plus End run.
- **Optional steps** run only if switched on when the run started; otherwise they're `skipped` and the run
  moves past them.
- **End run**: stops Fleet advancing it. Its sessions stay as ordinary sessions. No pause or resume.
- **Restart**: runs are rows. On start the runner reloads unfinished runs: a step that finished but whose next
  step hadn't started gets started; a running step whose turn was cut by the restart goes to Needs you (Fleet
  doesn't re-prompt); waiting runs keep waiting.
- Every change broadcasts `workflow_run` (the run's DTO) on the global `sessions` topic. Entering Needs you also
  sends the existing `session_notification` (reason `needs-you`), subject to the Desktop notifications
  preference and focus, for the step session the card is in.

### 4. `fleet_step_done` and hiding it

- Bridge: `POST /api/bridge/workflow/step-done {harnessSessionId, outcome, summary}`, loopback + process token like
  `fleet_message`. `WorkflowStepBridge` resolves the caller with `IHarnessCanvasCallerResolver`; the resolver
  gains `ViaParent` on its result, and the bridge refuses a call that came through a parent hop (a subagent) or
  from any session that isn't the running step's: "Only the session Fleet started for this step can finish it."
- Capability `HarnessCapabilities.SupportsWorkflowSteps`: the harness can give the step tool to step sessions
  only. OpenCode and OpenCode 2 say yes. On others the Run box says "Workflows aren't available on Claude Code.
  Pick OpenCode or OpenCode 2." and the API refuses the start.
- `HarnessSpawnOptions.WorkflowStep` / `HarnessResumeOptions.WorkflowStep` tell the adapter which sessions keep
  the tool. The orchestrator fills them from `sessions.workflow_run_id`.
- **OpenCode 1** (process started with `FLEET_WORKFLOWS=1`): every non-step session is created with
  `permission: [{fleet_step_done, *, deny}]`; every prompt to a non-step session carries
  `tools: {fleet_step_done: false}` (1.18.31 replaces `session.permission` with that map's rules, so resending is
  idempotent and covers sessions made before the switch). The recap's fork adds the same deny after its `* ask`.
  Step sessions get neither.
- **OpenCode 2**: non-step sessions are created with `AllowAll` then the deny rule (last matching rule wins);
  sessions resumed on a workflows server get `PATCH /api/session/:id {permissions}` with the same two rules.
- The plugin tools (`opencode/fleet/fleet-canvas.ts`, `opencode2/fleet/index.js`) exist only when
  `FLEET_WORKFLOWS=1`.

### 5. Model roles

- Preference `WorkflowModelRoles`, JSON per harness: `{ "opencode": { "strong": {"model": "provider/model",
  "effort": "high"}, … } }`. Kept in Fleet for the user, not the repo.
- Resolution for a step: the run's override for that role → the step's pinned model → the role's mapping → the
  composer's default model (none: the harness default). Effort likewise.
- Settings → Workflows → **Model roles**: a harness switch (the harnesses that support workflows), a row per role
  with the composer's `ModelSelector`-style picker ("Default model · now X" first), an effort picker, and "Used by"
  chips listing the steps that use the role in the workflows Fleet can see.
- The Run box has a **Models** menu that overrides each role for one run.
- Not in Stage 1: the "Pinned models in this repo" card (nice-to-have; add if time allows).

### 6. Built-in: Build a feature

Design (optional, off, strong, `fleet-mockups`) → Plan (strong, agent `plan`) → **You: Approve the plan** (Approve →
Implement; Send back with a note → Plan) → Implement (standard) → Review (strong, `fleet-code-review`; `changes` →
Implement, max 2) → Check it runs (optional, off, standard, `fleet-run`; `broken` → Implement, max 2) → **You:
Open the pull request** (Open PR → Push and open the PR; Keep the branch only → end) → Push and open the PR (fast;
pushes the branch and runs `gh pr create`, body from the plan file and Review's summary; its summary is the PR's
URL, so the run reads "PR #n opened"). Prompts from the mockup (`WF.dpi`), with Review's bar: `changes` only for
bugs or tests the plan named that are missing; nits in the summary with `pass`.

Fleet has no service that opens a PR, so "Open PR → end" as mocked would open nothing (decided with the
coordinator, 2026-09-23). A failed push or a signed-out `gh` is an ordinary step failure under Needs you. No
Fleet-side push or GitHub API call in Stage 1.

### 7. Client

- `useWorkflowsFeature()` (preference), rail item **Workflows** (`flow`-style icon, between Plugins and
  Automations) only when on; route `/workflows` with `[workflows-list][workflows-detail]` (added to AGENTS.md).
- `workflows-list`: "Built into Fleet", then "This repo · .weave/workflows" for the folder picked in the Run box
  (the composer's last folder); error rows for bad files.
- `workflows-detail`: header (name, "Built into Fleet" or the file path), blurb, steps strip with loop notes,
  facts (starts from, runs in, models · skills), recent runs, and the **Run box**: request, folder, worktree and
  base-branch chips from the session composer, a chip per optional step (on/off), Models menu, harness, Run.
- Sessions: a run shows as a group inside its project — a header row with the run's title and status (Working /
  Needs you / Done / Ended), then its step sessions nested under it, labelled by step.
- Step session conversation: a **stepper** under the header with every step's state; the first user message
  shows as "Workflow · step N of M" (no text added to the prompt for it).
- The **"You decide" card** in the conversation (Approve / Send back with a note / choices / End run) and the
  **Needs you card** for a step that stopped without an outcome (outcome buttons / End run). The dashboard's
  Needs you bucket includes sessions whose run is waiting on the user.
- Settings → Workflows: the switch card and the Model roles card.

### 8. Tests

- Application: runner (advance, loops and max, optional steps, You decide incl. note, idle without done, turn
  failed, restart recovery, end run, refused callers), YAML parser and validation (line numbers), role
  resolution, prompt building.
- Infrastructure: migration + repository round trip; OpenCode request shapes (deny at create, tools map on prompt,
  fork rules); OpenCode 2 create/patch rules.
- The recap's fork: its model request carries the same tool list as its parent's (no `fleet_step_done`), asserted
  on the request the model receives, not inferred from rule order.
- Integration (CI, live): OpenCode 1.18.31 and OpenCode 2 2.0.9 — a normal session's model request has no
  `fleet_step_done`; a step session's has it and can call it; the bridge records the outcome.
- Client: workflows store, run grouping, stepper, gate card, settings cards.
- Live on a scratch Fleet (port 5311, fake model 4995): Plan → Approve → Implement → Review `changes` → Implement →
  Review `pass` → Open PR; Send back with a note; a step idle without the tool lands under Needs you; a restart
  mid-run.

## Stage 2 — Bug triage and Review a pull request (outline)

- Starting from a GitHub issue (`starts-from: issue`, `{{issue}}`) or a PR (`starts-from: pr`, `runs-in:
  pr-branch`).
- Parallel steps (`parallel:` with at most one writer; others read-only or own worktree; continue on all/any).
- Bug triage built-in (`exit` outcomes that end the run early) and Review a pull request built-in.
- "Pinned models in this repo" card if not in Stage 1.

## Stage 3 — designer, Wait and triggers (outline)

- The Designer (flow view, inspector) and File view, and Duplicate a built-in into `.weave/workflows/`. May be
  dropped once the user has lived with hand-written YAML.
- Wait steps (GitHub checks via the smart-link polling, a timeout), Fix CI until green built-in.
- Automations that run a workflow (a new automation target).
- "Continue the previous step's session" as a per-step option, if the fix loops need it.
