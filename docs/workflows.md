# Workflows

A workflow is a short list of steps that Fleet runs for you. Each agent step is an ordinary Fleet session with its own
agent, model and optional skill. Fleet moves the run from step to step, which costs no tokens, and stops wherever a
step asks you. Workflows are experimental and off until you turn them on in **Settings → Workflows**.

Fleet ships one workflow, **Build a feature**. Your own live in the repository, one file each, under
`.weave/workflows/*.yaml`, so the people you work with get them with the code. In this version you write them by hand.

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
    prompt: Push this branch and open a pull request with gh pr create. Put its URL in summary.
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
| `skill`    | A skill the step should use. Fleet adds "Use the \<skill\> skill." to the prompt. A built-in skill has to be on in Settings → Skills. |
| `optional` | `true`, or a hint for when to switch it on (`For UI and new features`). Off unless switched on in the Run box. |
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
| `{{previous.summary}}`     | The last agent step's `fleet_step_done` summary, whole.                         |
| `{{steps.<id>.summary}}`   | That step's latest summary.                                                     |
| `{{run.branch}}`           | The run's branch.                                                               |
| `{{run.base}}`             | The branch the run's worktree started from.                                     |

Anything else is an error. A variable with nothing in it disappears, with the blank lines it leaves.

## How a run moves

Each agent step starts as a new session in the run's worktree. Its prompt is the step's `prompt` with the variables
filled in, then a note if the step was sent back, then one short footer that Fleet adds to step sessions only:

> This is one step of a Fleet workflow. When the step is finished, call fleet_step_done once, as your last action,
> with outcome set to one of: pass, changes. Put what the next step needs in summary.

The step ends when its session calls **`fleet_step_done(outcome, summary)`**. The outcome picks the next step, and the
whole summary is `{{previous.summary}}` for the next one. Anything longer belongs in a file the step writes, like a
plan, which the next agent reads with its own tools.

Fleet never moves a run on because a session went idle. If a step's turn ends without the tool, the step waits under
Needs you: pick an outcome on the card, or reply to the agent in that session, and if it then calls the tool the run
carries on. Fleet never prompts an agent on its own.

**End run** stops Fleet moving the run on. Its sessions stay as ordinary sessions. There is no pause or resume.

A run is saved as rows in Fleet's database. After a restart, a run carries on from where it was: a step that finished
but whose next step hadn't started goes on; a step whose turn the restart cut off waits for you; a run waiting on you
keeps waiting.

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
