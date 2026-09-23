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

## Follow-up: steps you finish together (Stage 1.5)

Mockup and decisions: https://claude.ai/artifact/Kr9fR1UTPW8iTT3XmS9gMw (Design together, Approve the plan, Implement
with you, Review with you). Decided with the user on 2026-09-23: only the user finishes a together step; the hand-off
is the step's declared files, brought up to date by one wrap-up turn, plus its summary and the user's note; Check with
me can be set in the Run box, on the approval card and in the run's header, and a change applies from the next step.

### 1. The file: `finish: you` and `writes:`

- Agent steps take `finish: you` (`agent` is the default; any other value is an error naming the line). Such a step
  still lists its `outcomes`; the user picks one when they move on, and `on`/`max` work as before.
- Agent steps take `writes:`, a list of paths relative to the run's worktree, with the prompt variables that are known
  when a run starts (`{{slug}}`, `{{run.branch}}`, `{{run.base}}`, `{{request}}` is refused: it isn't a path). An
  absolute path, `..`, or an unknown variable is an error with its line.
- New variables: `{{previous.files}}` (the previous agent step's declared files, "a.md and b.html") and
  `{{steps.<id>.files}}`. A step's files count once it has run.
- A line whose variables are all empty is left out (Stage 1 dropped the variable and the blank lines it left; a line
  such as "Read {{previous.files}} first." would otherwise stay half-empty). Documented in `docs/workflows.md`.
- Built-in: Design gets `finish: you`, `writes: [docs/design/{{slug}}.md, docs/design/{{slug}}.html]` and a prompt
  that names both. Plan gets `writes: [.weave/plans/{{slug}}.md]` and "Read {{previous.files}} first." plus
  `{{previous.summary}}`. Implement and Review read `{{steps.plan.files}}` rather than a hard-coded path.

### 2. Running a step you finish

- Which steps: when a visit **starts**, it's one the user finishes if the step says `finish: you` or the run's
  **Check with me** is on at that moment. The mode is saved on the visit (`workflow_run_steps.finish`), so switching
  Check with me later never changes a step that's running.
- Its session is made like any non-step session for tool purposes: `sessions.workflow_user_finishes = 1`, and the
  orchestrator passes `WorkflowStep = WorkflowRunId is not null && !WorkflowUserFinishes` on spawn and resume. So on
  OpenCode 1 it's created with the deny rule and every prompt carries `tools: {fleet_step_done: false}`; on OpenCode 2
  it's created AllowAll + deny. The session keeps `workflow_run_id`, so it still groups under its run.
- Its prompt is the step's prompt, the note, and the skill line, with **no footer**. A `fleet_step_done` from it is
  refused ("You and the user are working through this step together, and only the user can end it.").
- No idle watch: a turn ending is just a conversation turn. The run is `running`; the DTO says the current step is
  **With you** (the run row, the step row and the stepper show that). It is not Needs you and doesn't notify.
- A restart while a step you finish is open leaves it open (its turn stopped, but it's a conversation; the user
  replies). Only a cut-off wrap-up waits (below).

### 3. Move on and the wrap-up turn

- `POST /api/workflows/runs/{id}/move-on {outcome?, note?}`. Refused unless the current visit is a running step the
  user finishes; the outcome must be one of the step's (a single-outcome step needs none); a back outcome past the
  step's `max` is refused ("Review has sent the work back twice, the most it may in this run.").
- Fleet sends one prompt into the same session, and nothing else ever:
  "The user is moving on to Plan. Update docs/design/x.md and docs/design/x.html with everything agreed in this
  conversation, then reply with a short summary for Plan." With more than one outcome: "The user chose changes and is
  moving on to Implement. …". No declared files: "… Reply with a short summary for Implement." A note adds a line
  "Their note: …". The next step's name skips optional steps that are off.
- The visit goes to `wrapping-up` with the chosen outcome, the note (`hand_off_note`) and the prompt's message id
  (`wrap_up_message_id`, from `PromptSessionWithReceiptAsync`).
- The watch ties the turn to that prompt the way `SessionUpdates` does: it arms only on an assistant message whose
  `parentID` is the wrap-up's message id, so a turn already running when Move on was pressed doesn't count. When that
  turn's idle comes (after the same grace as Stage 1), the reply after the wrap-up message is read through
  `ISessionMessageProxy` (`SessionUpdateSender.LastReply`), whole, and becomes the visit's summary. Then the files
  check, then the next step.
- The wrap-up fails (`turn.failed` on it) or is cut off by a restart (a `wrapping-up` visit found by
  `RecoverAsync`): the run waits, kind `wrap-up-failed`: "Design's wrap-up failed: <error>. Reply to the agent in its
  session, or move on anyway." Replying puts the step back to With you when that reply's turn ends, and the bar's Move
  on runs a new wrap-up. **Move on anyway** goes on with the chosen outcome, no summary and the note.

### 4. The files check (every step with `writes:`)

- Before the run leaves an agent visit for another step (not for `end`), Fleet checks each declared path exists in
  the run's worktree (a file, not a folder; paths can't leave the worktree). This covers agent-finished steps too.
- Missing: the run waits, kind `missing-files`: "Plan declares .weave/plans/x.md, but it isn't in the run's worktree.
  Reply to the agent in its session, or move on anyway." The visit keeps its outcome and summary. When a turn in that
  session ends while it waits (the user replied), Fleet checks again and moves on if the files are there now; the
  watch is put back after a restart. **Move on anyway** skips the check.
- The visit records `files_checked` so the finished bar can say "Files checked. Plan has started in a new session."

### 5. What the next step gets

- `{{previous.summary}}`: the wrap-up reply for a step you finished, the `fleet_step_done` summary otherwise.
- `{{previous.files}}`: the declared paths.
- The user's Move on note, added after the prompt as "Note from the user:\n…" (like "Sent back with a note:"), only for
  the next agent step and not when that step is the same one again.

### 6. Check with me

- `WorkflowRunOptions.CheckWithMe` (saved in the run's options JSON, defaults false after a round trip).
- Run box: a switch "Check with me after each step" (off by default) → `StartWorkflowRunRequest.CheckWithMe`.
- Approval card: a You step's forward choice (a step, no note) gets two buttons when two or more enabled agent steps
  follow it: **Approve, let it run** ("Implement, Review and Check it runs go ahead on their own. You're asked again at
  Open the pull request.", built from the run) and **Approve, check with me after each step** ("Each step waits for
  you to move it on, and you can talk to the agent in between."). Answer carries `checkWithMe: true|false`, saved
  before the next step starts. Send back with a note and End run stay.
- Header: a switch in the stepper's run line, `PUT /api/workflows/runs/{id}/check-with-me {on}`, any time the run is
  unfinished. When it differs from the running step, a short note: "Check with me is on from Review. This step started
  on its own, so it still moves on when the agent reports it's done." / "Check with me is off from Review. You still
  finish this step, because it started with you."

### 7. The DTO and the client

- `WorkflowRunDto`: `checkWithMe`; `withYou` (the open step you finish: its step, session, files, whether it's
  wrapping up, and its moves: outcome, target title, loop `used`/`max`, allowed). Steps gain `finishYou` (declared),
  `withYou` (for a visited step, how its last visit started; for a pending agent step, `finishYou || checkWithMe`),
  `outcomes`. Sessions gain `files`, `filesChecked`, `promptMessageId`, `wrapUpMessageId`, `withYou`, `handOffNote`.
  `waiting` gains `files` (for a You step: the previous agent step's files) and the new kinds `missing-files` and
  `wrap-up-failed` with a `move-on-anyway` choice.
- `WorkflowFinishBar.vue` above the composer (in place of the run card while With you): "You finish Design · The agent
  can't end this step. Keep talking until it's right.", the note field, **Move on to Plan** or one button per outcome
  ("Pass: on to Open the pull request", "Changes: back to Implement (1 of 2)"), "Hands on: <files>", and the mockup's
  hand-off line. While wrapping up: "Wrapping up Design: the agent is updating its files and writing a summary for
  Plan. Then Fleet checks the files and starts Plan." After: "Files checked. Plan has started in a new session. [Open
  Plan]".
- The first message: "Workflow · step 1 of 7 · you finish this step", found by the visit's `promptMessageId` (a step
  you finish has no footer to find it by). The wrap-up prompt renders as "Fleet · you pressed Move on".
- The stepper marks steps the user finishes with a person icon (not once done). The run row and the step row say
  **With you** (accent) while one is open.
- When the run waits on a You step, the previous step's declared files open as kept tabs in the right-hand panel of
  the session the card is in, once per waiting visit.
- Needs you stays: You steps, no outcome, loop limit, start failed, missing files, failed or cut-off wrap-ups.

### 8. Storage

Migration `039_add_workflow_finish_you.sql`: `workflow_run_steps.finish` (`agent`/`you`), `prompt_message_id`,
`wrap_up_message_id`, `hand_off_note`, `files_checked`; `workflow_runs.waiting_kind` (Stage 1 rows derive it as
before); `sessions.workflow_user_finishes INTEGER NOT NULL DEFAULT 0`.

### 9. Tests

- Application: `finish: you` (no footer, the bridge refuses, no stall on idle, Move on with each outcome, loop max
  refused past it), the wrap-up (prompt text with outcome, files, note; a reply tied to another message doesn't count;
  summary from the tied reply; then the files check, then the next step with `{{previous.files}}`, summary and note),
  a missing file (waits; a later turn with the file moves on; Move on anyway), a failed and a cut-off wrap-up (waits;
  a reply puts it back With you; Move on anyway), Check with me from the Run box, the card and the header (the running
  step keeps its mode, the next one follows), restart with a step you finish open, the `writes:`/`finish:` parser with
  lines, the empty-line rule.
- Infrastructure: a `finish: you` session is created with the deny rule on OpenCode (create request + prompt tools
  map) and OpenCode 2 (AllowAll + deny); the orchestrator passes `WorkflowStep = false` for it on spawn and resume;
  migration + repository round trip of the new columns.
- Integration (live, CI): OpenCode 1.18.31 and OpenCode 2 2.0.9 — a step-you-finish session's model request has no
  `fleet_step_done`; the wrap-up's reply names the wrap-up prompt as its parent.
- Client: the finish bar (one outcome, two outcomes, loop count, wrapping up, moved on), the approval card's two
  approve choices, the header and Run box switches, the file opening next to a You step, With you labels.
- Live on a scratch Fleet (port 5321, fake model 4996), both harnesses: Design together → reply → Move on → the
  wrap-up edits the declared file → check → Plan starts with the files, the summary and the note; a missing file
  waits; Approve with check with me → Implement → Move on → Review → Changes → Implement → Review → Pass; the header
  switch mid-step; a restart during a wrap-up.

### Not in this change

- Parallel steps. When they come (Stage 2), only the branch in the run's worktree may declare files; a branch with its
  own worktree or a read-only one hands on its summary only, and declaring files there is a file error with its line.

## Stage 2 — Bug triage and Review a pull request (outline)

- Starting from a GitHub issue (`starts-from: issue`, `{{issue}}`) or a PR (`starts-from: pr`, `runs-in:
  pr-branch`).
- Parallel steps (`parallel:` with at most one writer; others read-only or own worktree; continue on all/any). Only
  the branch in the run's worktree may declare `writes:`; the others hand on their summary only, and declaring files
  on them is a file error naming the line.
- Bug triage built-in (`exit` outcomes that end the run early) and Review a pull request built-in.
- "Pinned models in this repo" card if not in Stage 1.

## Stage 3 — designer, Wait and triggers (outline)

- The Designer (flow view, inspector) and File view, and Duplicate a built-in into `.weave/workflows/`. May be
  dropped once the user has lived with hand-written YAML.
- Wait steps (GitHub checks via the smart-link polling, a timeout), Fix CI until green built-in.
- Automations that run a workflow (a new automation target).
- "Continue the previous step's session" as a per-step option, if the fix loops need it.
