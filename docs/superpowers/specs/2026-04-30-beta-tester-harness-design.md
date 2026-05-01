# Beta-Tester Harness — Design

> Status: draft
> Date: 2026-04-30
> Related constitution principles: §3 Harnesses define the experience, §6 Invisible infrastructure, §8 Data is a first-class output

## Problem

Fleet has stability and error-handling issues that the existing E2E suite does not surface. Two concrete examples reported by the user:

- The model selected when starting a chat is not persisted. After a page refresh the session reverts to the default model and subsequent prompts silently fail.
- Sub-agents do not persist their model selection either. There is currently no UI to edit a sub-agent profile.

The existing Playwright E2E rig swaps in a deterministic mock harness via `WebApplicationFactory<Program>`, but the scenarios are short, isolated, and do not exercise long-running, adverse, or fan-out behaviour. The user wants a feedback loop where Claude can drive a *live* fleet binary with a mocked harness, watch logs, exercise the SPA via Playwright, and record findings — acting as an automatic beta user.

## Goal

Provide a token-light feedback loop for Claude to:

1. Boot a live fleet binary in test-harness mode (no real model API calls).
2. Exercise UX flows via Playwright against the real Kestrel + SPA.
3. Watch server logs without flooding context.
4. Record findings as terse, structured markdown.
5. Promote stable repros to C# E2E regression tests so bugs cannot return.

Out of scope: real OpenCode integration, CI integration of beta-harness runs, a UI for scenario authoring.

## Constitution Alignment

- **§3 Harnesses define the experience** — the live test harness is a first-class harness implementation. The system and SPA must work through it without special cases.
- **§6 Invisible infrastructure** — the driver hides startup, log routing, and isolation behind one entry point.
- **§8 Data is a first-class output** — findings are persisted as structured markdown for review, with cross-references to log excerpts and screenshots.

## Architecture

```
Claude session
  │
  │  reads ──▶ tests/beta-harness/scenarios/<id>.md           (playbook + embedded JSON)
  │  drives ─▶ Playwright (TypeScript)                        (driver)
  │  tails  ─▶ tests/beta-harness/.runtime/fleet.log          (grep only, gitignored)
  │  writes ─▶ tests/beta-harness/findings/<date>-<id>.md     (results)
  ▼
fleet binary (live, real Kestrel + SPA)
  --harness=test  --data-dir tests/beta-harness/.runtime/data  --port 5099
  │
  └── LiveScenarioHarness (mock)
        ├── reads scenario config from tests/beta-harness/.runtime/scenarios/<id>.json
        ├── echoes requested provider/model in assistant output
        ├── supports long streaming, sub-agent fan-out
        └── supports adverse modes (drops, late events, errors)
```

The driver runs in a single Node/TypeScript process. Fleet runs as a child process. Playwright connects to the live Kestrel URL the same way a browser would. All runtime artefacts (logs, isolated data dir, materialised scenario JSON) live under `tests/beta-harness/.runtime/`, which is gitignored and recreated per run.

## Components

### 1. `--harness=test` flag in `WeaveFleet.Api/Program.cs`

A new CLI flag and equivalent environment variable (`FLEET_HARNESS=test`) that swaps the production `IHarness` / `IHarnessRuntime` registrations for the mock harness at startup.

- Default: production harnesses (OpenCode, ClaudeCode). The flag is opt-in.
- When active, fleet emits a loud warning at startup so the mode is unmistakable.
- Wired in `Program.cs` after `AddFleetInfrastructure`, by removing the production harness service descriptors and adding the live test harness.

### 2. Promoting the test harness to `src/`

The existing `tests/WeaveFleet.TestHarness` project is moved to `src/WeaveFleet.TestHarness` and renamed to make its first-class status explicit. The exact name is decided in the implementation plan; a candidate is `WeaveFleet.Infrastructure.MockHarness`.

- The E2E project (`tests/WeaveFleet.E2E`) updates its `<ProjectReference>` to the new location.
- Public API stays compatible: `TestHarness`, `TestHarnessRuntime`, `TestScenarioBuilder`, `FixtureLoader` keep their shape.

### 3. `LiveScenarioHarness`

A new layer on top of the promoted mock harness, designed for live use rather than per-test fluent configuration. Lives next to the harness it extends.

Capabilities beyond the existing `TestScenarioBuilder` model:

- **File-driven scenarios.** At session creation, the harness looks up a scenario id (passed in session metadata or a query parameter on `POST /api/sessions`) and loads the corresponding JSON from `tests/beta-harness/.runtime/scenarios/<id>.json`. The driver materialises these JSON files at startup by extracting the embedded ```json``` block from each `tests/beta-harness/scenarios/<id>.md` playbook (single source of truth — see Component 5). Editing a scenario file does not require restarting fleet.
- **Scripted streaming.** A scenario can describe a token-by-token response over an arbitrary duration (seconds to minutes) so we can stress streaming, SignalR fanout, and idle handling.
- **Sub-agent fan-out.** A scenario can declare that a prompt spawns child sessions, each with their own scripted response. Used to validate parent/child UX, telemetry attribution, and model inheritance.
- **Adverse modes.** Per scenario: drop one named event, delay events by N seconds, emit out-of-order events, fail mid-stream with a typed error, simulate a slow stream (one token per second).
- **Model echo.** The assistant message includes the requested provider/model verbatim. This is how we verify the user's reported model-selection bug end-to-end without real model APIs.
- **Fallback.** If a session is created with a scenario id the harness does not recognise, it falls back to a deterministic echo response so the session does not hang.

### 4. Playwright driver (`tests/beta-harness/`)

A TypeScript project, separate from `client/` and from the C# E2E rig. Pinned to the same Playwright version as `tests/WeaveFleet.E2E`.

Files:

- `start-fleet.ts` — spawns `dotnet run --project src/WeaveFleet.Api -- --harness=test --data-dir tests/beta-harness/.runtime/data --port 5099`, redirects stdout/stderr to `tests/beta-harness/.runtime/fleet.log`, polls `/healthz`, returns the base URL. Also supports attaching to an already-running fleet at a given URL.
- `stop-fleet.ts` — terminates the child process and any tracked harness instances.
- `helpers/` — small composable helpers Claude calls from short scripts:
  - `newSession({ scenarioId, provider, model })`
  - `selectModel(page, { provider, model })`
  - `waitForAssistantMessage(page, { timeoutMs })`
  - `tailLog({ grep, lines })` — read the most recent N matching lines from `fleet.log`
  - `recordFinding({ scenarioId, result, repro, evidence })` — appends a finding file
- `playwright.config.ts` — minimal, headless by default, headed via `HEADED=1`.

This is *not* a test runner. There is no `playwright test` invocation. The driver is a library Claude calls from short ad-hoc scripts.

### 5. Scenario playbooks (`tests/beta-harness/scenarios/*.md`)

One markdown file per playbook. **Single source of truth for both the human-readable playbook and the harness configuration.**

Structure:

- **Frontmatter** — `id`, `focus` (tags), `estimated_minutes`.
- **Embedded ```json``` block** — the harness configuration (scripted events, adverse modes, sub-agent declarations). The driver extracts this block at startup and materialises it into `tests/beta-harness/.runtime/scenarios/<id>.json`, which the live test harness reads.
- **Preconditions** — what must be true before the run starts.
- **Steps** — concrete UI/API steps Claude executes via the driver.
- **What to watch** — network calls, log lines, UI affordances that signal success or failure.
- **Known suspects** — pre-existing hypotheses about what might break.

This avoids drift between the playbook and the harness config: editing the markdown updates both. The materialised JSON under `.runtime/` is a build artefact, not checked in.

### 6. Findings store (`tests/beta-harness/findings/`)

One file per run: `YYYY-MM-DD-HHMM-<scenarioId>.md`. Fields:

- `result`: pass | suspected-bug | inconclusive
- `repro`: numbered, copy-pasteable steps
- `evidence`: log excerpts (line refs into `fleet.log`), screenshot paths, network response snippets
- `next probe`: what to investigate next, if anything

Findings accumulate over time. When a repro is stable and confirmed, it graduates to a C# E2E regression test in `tests/WeaveFleet.E2E/Tests/`.

## Data flow

1. Claude picks a playbook from `tests/beta-harness/scenarios/`.
2. Claude runs `node tests/beta-harness/start-fleet.ts` (or attaches to an already-running instance).
3. Claude executes the playbook through driver helpers in a short script.
4. The harness echoes scripted events back over SignalR. The SPA renders them.
5. Claude inspects the UI via Playwright and tails `fleet.log` by grep.
6. Claude writes a finding file with terse, structured output.
7. Stable repros graduate to C# E2E tests.

## Token-preservation tactics

| Tactic | Saves |
|---|---|
| Logs go to file; Claude reads by grep only | Avoids flooding context with verbose logs |
| Screenshots only on suspected bug | Avoids reflex captures |
| Playbooks live on disk; read once per run | No restating scenarios in chat |
| `--harness=test` mocks all model traffic | Zero real model API spend |
| Findings template is fixed | Output stays terse and structured |
| Headed mode off by default | Faster iteration, less debug noise |
| Per-run isolated `--data-dir` | No state leakage between runs |

## Error handling

- Fleet fails to start within 30s → driver dumps last 100 lines of `fleet.log`, exits non-zero.
- Playwright cannot reach base URL within 30s → driver kills fleet child, exits non-zero.
- Scenario file is malformed JSON → harness logs the error, falls back to echo response, session does not crash.
- Scenario id is unknown → harness falls back to echo response.
- Adverse-mode scenario kills the harness session intentionally → driver records the failure mode in the finding, not as a driver error.

## Initial scenario set

Concrete scenarios that ship with the first cut of the harness, in priority order:

1. **`model-persistence-refresh`** — the user's reported bug. Create session with explicit provider/model, send prompt, refresh page, verify model is still selected and assistant echo matches.
2. **`subagent-model-inheritance`** — the user's reported bug. Parent session spawns sub-agent, verify sub-agent uses the parent's model (or documented default), and that this is consistent across refreshes.
3. **`long-streaming`** — ~60s token-by-token streaming. Watch for SignalR fanout issues, memory growth, idle timeouts.
4. **`parallel-sessions`** — five sessions streaming simultaneously. Watch for resource leaks, per-session isolation, fanout fairness.
5. **`adverse-dropped-message-end`** — harness drops the `MessageEnd` event. Does the UI hang silently? Is there a watchdog or visible recovery?
6. **`adverse-late-events`** — events arrive 5–10s after the next user prompt fires. Does message ordering stay correct?
7. **`subagent-fanout`** — one parent, two children streaming concurrently. Watch parent/child UX and telemetry attribution.
8. **`model-removed-mid-flight`** — session was using model X; X is removed from config; next prompt is sent. What happens?

## Testing the harness itself

- A C# unit test asserts that `--harness=test` swaps the production harness registrations for the live test harness.
- A C# E2E smoke test instantiates `LiveScenarioHarness` with a basic scenario to verify the new code path stays alive across changes.

The driver and playbooks themselves are not test-covered — they are tools for a human-in-the-loop (Claude) to use, not a test suite.

## Open implementation choices (deferred to plan)

- Exact name and namespace for the promoted harness assembly.
- Whether `LiveScenarioHarness` is a separate class in the same project or a new sub-project.
- How a scenario id is conveyed from `POST /api/sessions` to the harness (header, metadata field, or session id prefix).

## Risks

- Promoting the test harness to `src/` increases surface area shipped in production binaries. Mitigation: `--harness=test` is opt-in and emits a startup warning. Long term we may decide to ship a separate test build, but that is out of scope here.
- TypeScript driver duplicates some setup logic with the C# E2E rig. Mitigation: keep the driver thin and avoid re-implementing C# scenario logic — the harness owns scenario state.
- Adverse modes could produce false positives if scenario authors get the expected outcome wrong. Mitigation: every scenario lists explicit "what to watch" assertions, and findings record both expected and observed.
