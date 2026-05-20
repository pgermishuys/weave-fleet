# OpenCode Harness UI Smoke Tests

## TL;DR
> **Summary**: Add opt-in E2E smoke tests that exercise the Fleet UI against a real OpenCode harness, gated by `FLEET_HARNESS_SMOKE=1`.
> **Estimated Effort**: Medium

## Context
### Original Request
Fleet needs UI-level smoke tests that verify real harnesses work end-to-end through the browser, not just via unit/contract tests. OpenCode is the first target. Tests must be manual/opt-in and must not disturb existing deterministic E2E tests.

### Key Findings
- `FleetWebApplicationFactory` (lines 96-129) explicitly removes all `IHarness`/`IHarnessRuntime` registrations and replaces them with `TestHarness`. This is the key gate to change.
- `E2ETestBase` provides `ConfigureScenario()` which only makes sense for `TestHarness`. Smoke tests won't use this.
- Existing page objects (`FleetDashboardPage`, `NewSessionDialog`, `SessionDetailPage`) already cover the create-session + send-prompt + view-response flow (see `GoldenPathTests`).
- The factory uses isolated SQLite DBs and ephemeral Kestrel ports — this can be reused for smoke tests.
- OpenCode harness types: `OpenCodeHarness`, `OpenCodeHarnessRuntime` in `WeaveFleet.Infrastructure.Harnesses.OpenCode`.

### User Decisions Captured
- OpenCode must be available on `PATH` on the developer machine running the smoke test.
- Each smoke test run should create a dedicated temporary working directory.
- OpenCode 1.15.5 does not accept a positional working-directory argument for `serve`; start `opencode serve` with the process working directory set to the temporary directory.
- The smoke test setup should use settings/configuration to enable only the harness under test and set it as the default harness.
- The new-session dialog should therefore auto-select OpenCode as the first/default harness in the list; the test does not need bespoke harness-picking logic unless the UI requires it.
- The assistant assertion can be structural: after sending a prompt, verify there is a user message plus an assistant/agent response. Prefer not to assert exact LLM text unless the implementation can make it reliable.
- Longer timeouts are expected and acceptable for this smoke category.

## Objectives
### Core Objective
Enable developers to run `FLEET_HARNESS_SMOKE=1 dotnet test tests/WeaveFleet.E2E --filter "Category=HarnessSmoke"` and verify OpenCode works through the Fleet UI.

### Deliverables
- [x] `SmokeFleetWebApplicationFactory` — factory variant that preserves real harness registrations
- [x] `HarnessSmokeTestBase` — base class with env-var skip guard
- [x] `OpenCodeSmokeTests.cs` — smoke test class
- [x] Documentation in `tests/WeaveFleet.E2E/README.md` (append section)

### Definition of Done
- [x] `dotnet build tests/WeaveFleet.E2E` succeeds
- [x] Without `FLEET_HARNESS_SMOKE=1`, smoke tests are skipped
- [x] With `FLEET_HARNESS_SMOKE=1` + valid OpenCode config, tests pass against real harness
- [x] Existing E2E tests remain unaffected

### Guardrails (Must NOT)
- Must NOT run in CI by default
- Must NOT modify existing `FleetWebApplicationFactory` behavior
- Must NOT require credentials for normal `dotnet test` runs
- Must NOT replace deterministic E2E coverage

## TODOs

- [x] 1. Create `SmokeFleetWebApplicationFactory`
  **What**: A new factory class that inherits from `WebApplicationFactory<Program>`, reuses the DB/Kestrel setup from `FleetWebApplicationFactory` but does NOT remove production harness registrations. Still overrides DB paths and uses ephemeral port. Adds smoke-specific configuration so OpenCode is enabled and set as the default/first harness. Creates a dedicated temporary working directory for the test run and configures OpenCode to use that path as its process working directory. Assumes the `opencode` executable is available on `PATH`.
  **Files**: `tests/WeaveFleet.E2E/Infrastructure/SmokeFleetWebApplicationFactory.cs`
  **Acceptance**: Factory boots with real `OpenCodeHarness` registered in DI when `FLEET_HARNESS_SMOKE=1`, with OpenCode enabled/defaulted and a per-run temp working directory configured.

- [x] 2. Create `HarnessSmokeTestBase`
  **What**: Abstract base class similar to `E2ETestBase` but: uses `SmokeFleetWebApplicationFactory`, adds `[Trait("Category", "HarnessSmoke")]`, includes a skip guard checking `FLEET_HARNESS_SMOKE=1`. Does NOT expose `ConfigureScenario`. Uses longer timeouts (real harness responses are slower).
  **Files**: `tests/WeaveFleet.E2E/Infrastructure/HarnessSmokeTestBase.cs`
  **Acceptance**: Tests inheriting this base skip when env var is unset.

- [x] 3. Create `OpenCodeSmokeTests`
  **What**: Test class with a single smoke test: navigate to dashboard, create session (OpenCode should be auto-selected because settings enable/default it), send a simple prompt such as "Say hello", wait for assistant response to appear, verify the conversation contains at least the user message and one assistant/agent response, verify no error toast, verify session returns to idle. Use existing page objects. Set timeout to ~60s or more for real LLM response.
  **Files**: `tests/WeaveFleet.E2E/Tests/OpenCodeSmokeTests.cs`
  **Acceptance**: Test passes with real OpenCode + valid provider credentials.

- [x] 4. Update E2E README with smoke test docs
  **What**: Append a "Harness Smoke Tests" section documenting: purpose, required env vars (`FLEET_HARNESS_SMOKE`, provider API keys, harness settings), `opencode` on `PATH`, per-run temporary working directory behavior, run command, expected behavior.
  **Files**: `tests/WeaveFleet.E2E/README.md`
  **Acceptance**: Developer can follow docs to run smoke tests.

- [x] 5. Verify CI exclusion
  **What**: Confirm that CI test commands use `--filter "Category=E2E"` or similar that excludes `Category=HarnessSmoke`. If not, add explicit exclusion filter.
  **Files**: `.github/workflows/` (inspect, modify if needed)
  **Acceptance**: CI pipelines do not run `HarnessSmoke` tests.

## Verification
- [x] `dotnet build tests/WeaveFleet.E2E` compiles cleanly
- [x] `dotnet test tests/WeaveFleet.E2E --filter "Category=E2E"` passes (no regression)
- [x] `dotnet test tests/WeaveFleet.E2E --filter "Category=HarnessSmoke"` skips without env var
- [x] `FLEET_HARNESS_SMOKE=1 dotnet test tests/WeaveFleet.E2E --filter "Category=HarnessSmoke"` passes with valid OpenCode setup

## Risks & Mitigations

| Risk | Mitigation |
|------|-----------|
| OpenCode requires specific binary/path on machine | Document prerequisites; test logs clear error if binary missing |
| Real LLM responses are non-deterministic | Assert only structural properties (response exists, no error), not content |
| Timeouts with slow providers | Use 60s+ page timeout for smoke tests |
| Factory may need harness-specific config (ports, paths) | Use smoke-specific settings overlay; configure OpenCode as enabled/default harness |
| Temp working directory lifecycle leaks | Create per-run temp dir and clean it up via factory/base disposal where safe; preserve on failure only if useful for debugging |
| `NewSessionDialog` may not expose harness selection | Expected: settings make OpenCode first/default so dialog auto-selects it; only add explicit selection if UI requires it |
