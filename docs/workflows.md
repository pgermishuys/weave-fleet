# Workflows

A workflow is a short list of steps that Fleet runs for you. Each agent step is an ordinary Fleet session with its own
agent, model and optional skill. Fleet moves the run from step to step, which costs no tokens, and stops wherever a
step asks you. Workflows are experimental and off until you turn them on in **Settings → Workflows**.

Fleet ships two workflows. **Build a feature** turns a sentence into a reviewed pull request, stopping once for you
to approve the plan. **Fix a bug** finds the root cause first (with the fleet-debug skill), stops once for you to agree
with it, then fixes it with a test that fails first, reviews the fix and opens a pull request.

Your own live in the repository, one file each, under `.weave/workflows/*.yaml`, so the people you work with get them
with the code. Make one with **New workflow** or **Duplicate** in the Workflows page and change it in the designer, or
write the file by hand. See [The designer](#the-designer). Fleet can also draft one from a session or a sentence: see
[Drafting a workflow](#drafting-a-workflow).

## A workflow file

```yaml
name: Weekly dependency bump
description: Bumps the dependencies and checks nothing broke.
placeholder: "Which packages? e.g. everything in client/"
starts-from: sentence
runs-in: new-worktree
steps:
  - id: bump
    title: Bump
    agent: build
    model: standard
    prompt: |
      Bump the dependencies in {{request}}. Commit the lock file.
    outcomes: [done]

  - id: tests
    title: Run the tests
    agent: build
    model: github-copilot/gpt-5.4-mini
    effort: low
    prompt: |
      The request: {{request}}
      Run the test suites and fix what the bump broke.

      {{previous.summary}}
    outcomes: [green, red]
    on: { red: bump, max: 2 }

  - id: ok
    title: Keep it?
    you: Open a pull request for the bump?
    choices:
      Open PR: pr
      Try again with a note: { to: bump, note: true }
      Drop it: end

  - id: pr
    title: Open the PR
    model: fast
    finish: agent
    prompt: |
      The request: {{request}}
      Push this branch and open a pull request with gh pr create. Put its URL in summary.
    outcomes: [opened]
```

### Top level

| Key           | What it is                                                                                     |
|---------------|------------------------------------------------------------------------------------------------|
| `name`        | Required. What the library calls it.                                                           |
| `description` | A sentence or two for the library.                                                             |
| `placeholder` | The hint in the Run box.                                                                       |
| `starts-from` | `sentence` (the default): the run starts from what you type. The only value in this version.  |
| `runs-in`     | `new-worktree` (the default): one worktree and branch per run, from the base branch you pick. Every step works in it. The only value in this version. |
| `steps`       | Required. The steps, in order.                                                                 |

### Agent steps

| Key        | What it is                                                                                                  |
|------------|-------------------------------------------------------------------------------------------------------------|
| `id`       | Required. Lowercase letters, digits and dashes; unique in the file. `end` is taken.                         |
| `title`    | Required. What the stepper and the Sessions list call it.                                                   |
| `agent`    | The harness agent, e.g. `build` or `plan`. Left out, the harness's default agent.                           |
| `model`    | Required. A role (`strong`, `standard`, `fast`) or an exact `provider/model`. See [Models](#models).      |
| `effort`   | The model's reasoning effort (its variant), e.g. `low` or `high`.                                           |
| `skill`    | A skill the step should use. Fleet adds "Use the \<skill\> skill." to the prompt. A built-in skill has to be on in Settings → Skills, both when the run starts and when the step starts (see [A skill turned off](#a-skill-turned-off)). |
| `optional` | `true`, or a hint for when to switch it on (`For UI and new features`). Off unless switched on in the Run box. |
| `finish`   | `you`: you work through the step together and only you end it. `agent`: the agent always ends it with `fleet_step_done`, even with Check with me on. Left out, the agent ends it unless Check with me is on. See [Steps you finish](#steps-you-finish). |
| `writes`   | The files the step writes, relative to the run's worktree, e.g. `docs/design/{{slug}}.md`. Fleet checks they exist before the next step starts, then commits them. See [Declared files](#declared-files). |
| `prompt`   | Required. What the agent should do. See [Variables](#variables).                                          |
| `outcomes` | Required. The words the agent can finish with, e.g. `[pass, changes]`.                                      |
| `on`       | Where an outcome leads: a step's id or `end`, plus `max` for loops. Outcomes not listed go to the next step. |

An outcome that goes back to an earlier step (or to the same step) is a **loop**, and the step needs `max`: how many
times it may send work back in one run. The next time, the run stops and asks you where to go.

### You decide steps

| Key       | What it is                                                                                          |
|-----------|-----------------------------------------------------------------------------------------------------|
| `id`      | Required.                                                                                           |
| `title`   | What the card and the stepper call it. Default: "You decide".                                       |
| `you`     | Required. The question, e.g. `Build it this way?`.                                                  |
| `choices` | Required. Each label leads to a step's id or `end`. `{ to: <step>, note: true }` asks for a note, which goes into that step's next prompt as "Sent back with a note:". |

The run stops under **Needs you** with a card in the conversation of the step before it, and the desktop
notification you already have (Settings → Features) tells you.

### Variables

| Variable                   | What it becomes                                                                 |
|----------------------------|---------------------------------------------------------------------------------|
| `{{request}}`              | What you typed in the Run box.                                                  |
| `{{slug}}`                 | The request as a short slug, e.g. `press-see-every-keyboard-shortcut`.          |
| `{{previous.summary}}`     | The last agent step's summary, whole: its `fleet_step_done` summary, or for a step you finished, the agent's reply to the wrap-up. |
| `{{previous.files}}`       | The files the last agent step declares, e.g. `docs/design/x.md and docs/design/x.html`. |
| `{{steps.<id>.summary}}`   | That step's latest summary.                                                     |
| `{{steps.<id>.files}}`     | The files that step declares, once it has run.                                  |
| `{{run.branch}}`           | The run's branch.                                                               |
| `{{run.base}}`             | The branch the run's worktree started from.                                     |

Anything else is an error.

**Put `{{request}}` in every agent step's prompt.** Each step is a new session, and it knows only what its own prompt
says. A request such as "don't install packages or run the test suite" reaches a step only if its prompt has the
request in it, so Build a feature starts every later step with `The request: {{request}}`.

A line whose variables are all empty is left out, with the blank lines that leaves: with
Design off, `Read {{previous.files}} first.` isn't in Plan's prompt at all. A line with no variables is always kept,
and a line where only some of its variables are empty keeps the rest.

A declared file (`writes:`) can use `{{slug}}` and `{{run.branch}}`: the variables a run knows before any step starts.

## How a run moves

Each agent step starts as a new session in the run's worktree. Its prompt is the step's `prompt` with the variables
filled in, then a note if the step was sent back, then one short footer that Fleet adds to step sessions only (a step you finish
has none, see [Steps you finish](#steps-you-finish)):

> This is one step of a Fleet workflow. When the step is finished, call fleet_step_done once, as your last action,
> with outcome set to one of: pass, changes. Put what the next step needs in summary.

The step ends when its session calls **`fleet_step_done(outcome, summary)`**. The outcome picks the next step, and the
whole summary is `{{previous.summary}}` for the next one. Anything longer belongs in a file the step writes, like a
plan, which the next agent reads with its own tools.

A step that declares files (`writes:`) hands them on too: before the next step starts, Fleet checks each one exists
in the run's worktree. See [Declared files](#declared-files).

Fleet never moves a run on because a session went idle. If a step's turn ends without the tool, the step waits under
Needs you: pick an outcome on the card, or reply to the agent in that session, and if it then calls the tool the run
carries on. Fleet never prompts an agent on its own.

**End run** stops Fleet moving the run on. Its sessions stay as ordinary sessions. There is no pause or resume.

A run is saved as rows in Fleet's database. After a restart, a run carries on from where it was: a step that finished
but whose next step hadn't started goes on; a step whose turn the restart cut off waits for you; a run waiting on you
keeps waiting.

## Steps you finish

Some steps are a conversation: a design is worked out with you, not handed over. A step with `finish: you` is one you
finish yourself:

- Its session doesn't have `fleet_step_done`, and its prompt has no footer, so the agent can't end the step or keep
  offering to. You talk to it in that session for as long as you like.
- A **You finish Design** bar above the composer has a note for the next step and **Move on to Plan**. A step with
  more than one outcome has one button per outcome: **Pass: on to …** and **Changes: back to Implement (1 of 2)**. A
  loop's `max` still counts; when it's used up, that button says so.
- The run row and the step row say **With you**. It isn't Needs you: it's an ordinary conversation.

**Move on** sends one prompt into the same session, the only text Fleet ever sends into a step you finish:

> The user is moving on to Plan. Update docs/design/x.md and docs/design/x.html with everything agreed in this
> conversation, then reply with a short summary for Plan.

With more than one outcome it says which you chose ("The user chose changes and is moving on to Implement."); your
note is added as "Their note: …"; with no declared files it only asks for the summary. The run moves on when the turn
that answers that prompt ends. The agent's reply is `{{previous.summary}}` for the next step, and your note is added to
the next step's prompt as "Note from the user:".

If the wrap-up fails, or a restart cuts it off, the step waits under Needs you. Reply to the agent in its session (the
step is yours to move on from again once that reply's turn ends), or press **Move on anyway**, which goes on without a
summary.

### Check with me after each step

A run can make every agent step one you finish: switch on **Check with me after each step** in the Run box, choose
**Approve, check with me after each step** at the approval, or use the switch in the run's header at any time. A change
applies from the next step: the step that's running finishes the way it started, so its agent is never given or denied
the step tool in the middle of a turn.

Check with me applies only to steps whose file doesn't say `finish:`. A step with `finish: agent` always ends with
`fleet_step_done`, so a step that just carries out what you already chose, such as Build a feature's **Push and open
the PR** after you pick **Open PR**, doesn't ask you twice. A step with `finish: you` is always yours.

## Declared files

A design or a plan lives in files, not in a summary. A step says which files it writes:

```yaml
  - id: design
    title: Design
    finish: you
    writes:
      - docs/design/{{slug}}.md
      - docs/design/{{slug}}.html
```

Paths are relative to the run's worktree: a run has one worktree and branch, and every step works in it, so Plan finds
Design's files in its own folder, committed or not. Before the next step starts, Fleet checks every declared file
exists, for every step that declares any, whoever finishes it. If one is missing, the step waits under Needs you: reply
to the agent in its session (when that reply's turn ends, Fleet looks again and goes on if the files are there now), or
press **Move on anyway**.

Once the files pass the check, Fleet commits the ones that are new or changed, and only those: `git add` and
`git commit` with those paths, as `docs: design (x.md, x.html)`, with the repository's own git identity and hooks.
Anything else the agent changed or staged stays as it was. If the agent already committed them, there's nothing to
commit and Fleet doesn't. Fleet never force-adds, so a path git ignores stays uncommitted: a workflow that shouldn't
ship its design docs can write them to an ignored path. Move on anyway skips the check, and so the commit.

If the commit fails (no git identity, a hook that refuses it), the run still goes on. The card in the step's session
says the files were checked and why Fleet couldn't commit them; when it worked, it says what they were committed as.

What's committed goes in the pull request.

When the run stops at a You decide step, the files the step before it declares open next to the card: Approve the plan
shows the plan.

## Models

Built-in steps ask for a **role**, not a model, because the same model has a different id through each provider:

| Role       | For                                                   | Build a feature uses it for     |
|------------|-------------------------------------------------------|---------------------------------|
| `strong`   | Judgement: deciding what to build and whether it's right | Design, Plan, Review          |
| `standard` | Doing the work: writing and running code              | Implement, Check it runs        |
| `fast`     | Reading and sorting: short tasks with a clear answer  | Push and open the PR            |

Settings → Workflows → **Model roles** maps each role to one of your models, per harness, with an effort. A role you
haven't set uses the composer's default model. The mapping is yours, kept in Fleet, not in the repository. The Run
box's **Models** menu changes the roles for one run. A step can pin an exact `provider/model` instead; anyone without
that model can't run it. If a step's model isn't available when you press Run, the run doesn't start and says which
step.

### A skill turned off

A run checks every step's built-in skill when it starts, and each step's again as that step starts, because a run can
wait for days. If the skill is off by then, the step doesn't start: it waits under **Needs you** with "Review uses
fleet-code-review, which is now off." **Retry** starts it once the skill is on again (Settings → Skills); **Start
without it** starts it without the skill, and every later visit of that step in the run does too.

### A step that couldn't do its job

An outcome called `failed` that ends the run makes the run's result say why: the step's title and the first line of
its summary, e.g. "Push and open the PR failed: there's no remote called origin." Build a feature's **Push and open
the PR** has it: `outcomes: [opened, failed]` and `on: { failed: end }`.

## The designer

A workflow in `.weave/workflows/` opens in the designer. Built-ins don't: **Duplicate to this repo** copies one into
the repository picked in the Run box, under the name you give it, and opens the copy. **New workflow** starts from the
smallest workflow that runs: one agent step, **Do the work**, on Standard, with `The request: {{request}}` and the
outcome `done`. Both make `.weave/workflows/<name>.yaml` in that repository's checkout, uncommitted; a name another
workflow or file already has is refused.

- **Designer** shows the steps top to bottom with their loops drawn beside them. **+** between two steps adds an
  agent step or a You decide step there. The inspector edits the selected step, or, with none selected (or from the
  **Starts from** box), the workflow's name, description and Run box hint.
- A step's **id** is what loops, choices and `{{steps.<id>.…}}` point at, so renaming a step's title changes nothing
  else. Changing the id on purpose changes every reference to it.
- A step has one `max` for its loops; with two loop outcomes, both rows edit it.
- **File** shows the YAML, and you can edit it there. What the designer saves is exactly what the File view shows.
- Fleet checks the file with the same parser runs use as you edit. Errors show on the step, under the canvas and on
  their line in the File view, and **Save** waits until there are none.

Saving from the designer writes the whole file in Fleet's layout, the one this page uses. A file with comments says so
when it opens, and the first save from the designer lists the comments it would remove. To keep them, edit in the
File view: it saves your text exactly as it is. Saving never commits: the file is an ordinary change in your
checkout. If the file changed on disk after you opened it, Save says so and offers **Reload** or **Keep mine**.

A run keeps the workflow it started with, so saving doesn't change runs in progress. **Try it** opens the Run box for
the file as saved; with unsaved edits it asks you to save first.

## Drafting a workflow

Fleet can write a first draft for you, from a session or from a sentence. Either way it asks the model **once**, and the
draft opens in the designer **unsaved**: nothing is written to the repository until you press **Save**.

- **Save as workflow…** in a session's menu asks the model to describe the process that session followed as a
  workflow: steps in general terms, so it's reusable rather than a replay of the session; roles, not pinned models;
  and a You decide step wherever you steered or approved. The question is asked **off the record**, the way the
  welcome-back recap is: OpenCode answers from a fork of the session with its last model, agent and effort, so the
  conversation is read from the provider's cache, and the fork is deleted afterwards. OpenCode 2 uses its own
  `generate`, which leaves no trace. The session's history doesn't change. A harness that can't ask off the record
  (Claude Code, Pi) doesn't offer it.
- **New workflow → Describe it** takes a sentence ("What should it do?") and asks the harness you pick, on its
  **Standard** model (Settings → Workflows → Model roles). There's no session to read, so Fleet asks a throwaway one and
  deletes it.

The answer goes through the same check the designer uses. If it has errors, Fleet asks once more in the same
conversation, with the errors, and then opens what it has. A valid draft opens in the Designer; one that still has
errors opens in the File view with them, and Save waits until they're fixed. A banner says where it came from and what
it cost:

> Drafted from Fix the login bug. Review it before saving. Nothing is saved until you press Save. Asked the model
> twice: its first answer had errors · 7,584 tokens, 6,200 from the cache.

OpenCode reports the tokens; OpenCode 2 doesn't, so its banner has no count. Save creates
`.weave/workflows/<name>.yaml` under the same rules as New workflow: a name that's taken is refused, so rename it in
Workflow settings and save again. Nothing runs in the background and no text is added to any session.

## Running a workflow on a schedule

An automation can run a workflow instead of starting a session. In the automation composer, set **Runs** to **a
workflow**, pick one from the Library for the chosen repository, and switch on its optional steps as in the Run box.
The When, folder and base branch chips work as they do for any automation; each run gets its own worktree. The
Library's **Repeat on a schedule…** opens the composer with the workflow and what's in the Run box filled in.

- The automation's message is the run's `{{request}}`.
- A run starts the same way as from the Run box, so it has the same checks: Workflows on, a harness that runs
  workflows, a file without errors, and every step's model and skill. Model roles are read from Settings → Workflows
  when the automation fires, not when it was saved.
- **Check with me** is always off, since nobody is watching. A You decide step still stops the run under Needs you,
  with the usual notification.
- A run never stacks on an unfinished one. A firing while the last run is running, with you or waiting on you is
  **Skipped**, with the reason: "Skipped: the last run is still waiting on you (Approve the plan)." Run now isn't held
  back this way: you asked for it.
- A firing that can't start is Skipped too, with why: "Skipped: Workflows are turned off in Settings.", or the start's
  own error, e.g. a step's model that isn't available.
- The automation's run opens the workflow run, and shows its state (Running, Needs you, Done, Ended). The run says
  **Started by** the automation in the Library, in its header and on its group in Sessions.

With Workflows off, the composer doesn't offer a workflow, and an automation that runs one says its runs are skipped
until they're back on.

## Harnesses

Workflows run on **OpenCode** and **OpenCode 2**. `fleet_step_done` is a tool only step sessions see: with workflows on,
every other session on the same OpenCode process or server has a rule that denies it, and Fleet refuses a call from any
session that isn't the running step's own, including a step's subagents. Claude Code and Pi can't hide a tool from one
session and not another, so workflows aren't available on them.

## Errors

A file that doesn't read shows in the library with each problem and its line, e.g.

```
.weave/workflows/deps.yaml, line 12: review: "implment" isn't a step in this workflow.
```

The other workflows still load, and a workflow with errors can't run.
