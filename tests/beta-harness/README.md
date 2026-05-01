# beta-harness

Token-light loop where Claude drives a live fleet binary against scripted scenarios. Not a test runner — a library called from short ad-hoc scripts.

See `docs/superpowers/specs/2026-04-30-beta-tester-harness-design.md` for the why.

## One-time setup

```powershell
cd tests/beta-harness
bun install
```

## Run a scenario

```powershell
# Materialise playbook markdown -> .runtime/scenarios/*.json
bun run materialise-scenarios

# Start fleet in test-harness mode (logs -> .runtime/fleet.log, isolated data dir)
bun run start-fleet
# in another terminal, drive the SPA with helpers (see helpers/)
```

`bun run stop-fleet` terminates the fleet child and cleans up.

## Layout

```
tests/beta-harness/
├── start-fleet.ts             # spawn the fleet binary, wait for /healthz
├── stop-fleet.ts              # tear it down
├── materialise-scenarios.ts   # extract ```json blocks from scenarios/*.md
├── helpers/                   # short composable helpers Claude calls
├── playwright.config.ts       # headless by default, HEADED=1 to debug
├── scenarios/                 # playbook markdown — single source of truth
├── findings/                  # YYYY-MM-DD-HHMM-<scenarioId>.md, accumulates over time
└── .runtime/                  # gitignored: fleet.log, data/, scenarios/*.json
```

## Findings

Every run records a finding under `findings/`. Stable repros graduate to C# E2E tests in `tests/WeaveFleet.E2E/Tests/`.
