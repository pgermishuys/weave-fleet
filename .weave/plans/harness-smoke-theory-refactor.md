# Harness Smoke Theory Refactor

## TL;DR
> **Summary**: Refactor `OpenCodeSmokeTests` into a data-driven `[Theory]` so additional harness types (claude-code, nucode, future) can be added as rows without duplicating test logic.
> **Estimated Effort**: Medium

PR: https://github.com/pgermishuys/weave-fleet/pull/133

## Context
### Original Request
Convert the existing OpenCode-specific smoke test into a theory-based harness smoke contract where each harness is a data row.

### Key Findings
- `SmokeFleetWebApplicationFactory` is an `IClassFixture` — shared across all tests in the class. It hardcodes OpenCode as default, disables other harnesses in `ConfigureSmokeDataAsync()`.
- `HarnessSmokeTestBase` takes the factory in its constructor; it's harness-agnostic already.
- The factory validates `OpenCodeHarness`/`OpenCodeHarnessRuntime` are registered and sets preferences (`opencode.enabled=true`, others disabled, `defaultHarnessType=opencode`).
- The test asserts `harnessType == "opencode"` on the created session — this needs parameterization.
- xUnit `[Theory]` with `[MemberData]` works, but `IClassFixture` shares one factory instance across all theory rows — problematic if each row needs different preference state.

## Objectives
### Core Objective
Make harness smoke tests data-driven with per-row factory isolation so each harness type gets its own server instance with correct preferences.

### Deliverables
- [ ] `HarnessSmokeSpec` record model
- [ ] Parameterized factory or per-row factory creation pattern
- [ ] Generic theory test class replacing `OpenCodeSmokeTests`
- [ ] OpenCode as first (and initially only non-skipped) row

### Definition of Done
- [ ] `dotnet build tests/WeaveFleet.E2E` succeeds
- [ ] `FLEET_HARNESS_SMOKE=1 dotnet test tests/WeaveFleet.E2E --filter "Category=HarnessSmoke"` passes with OpenCode row
- [ ] Additional rows can be added by appending to `MemberData` without code changes

### Guardrails (Must NOT)
- Must not break existing non-smoke E2E tests
- Must not require multiple harness binaries installed to run a single row
- Must not use `IClassFixture` for the factory (per-row isolation needed)

## TODOs

- [ ] 1. Create `HarnessSmokeSpec` model
  **What**: A record in `tests/WeaveFleet.E2E/Infrastructure/` with properties: `HarnessType` (string, e.g. "opencode"), `EnabledPreferenceKey` (e.g. "opencode.enabled"), `DisplayName` (for test display), `DisabledHarnessKeys` (list of preference keys to disable). Implement `IXunitSerializable` so xUnit can serialize it for `[Theory]` rows.
  **Files**: `tests/WeaveFleet.E2E/Infrastructure/HarnessSmokeSpec.cs`
  **Acceptance**: Compiles; can be used as `[MemberData]` parameter.

- [ ] 2. Make `SmokeFleetWebApplicationFactory` parameterizable
  **What**: Add a `ConfigureForHarness(HarnessSmokeSpec spec)` method (or accept spec in constructor). Replace hardcoded `opencode.enabled=true` / others disabled with spec-driven preference writes. Keep the `IHarnessRegistry` validation generic: assert `registry.GetByType(spec.HarnessType) is not null` and `registry.GetRuntimeByType(spec.HarnessType) is not null` instead of casting to `OpenCodeHarness`. Remove `IClassFixture` usage — the factory will be created/disposed per test method.
  **Files**: `tests/WeaveFleet.E2E/Infrastructure/SmokeFleetWebApplicationFactory.cs`
  **Acceptance**: Factory can boot for any registered harness type without OpenCode-specific checks.

- [ ] 3. Update `HarnessSmokeTestBase` for per-test factory lifecycle
  **What**: Since we're dropping `IClassFixture`, the base class should accept a factory that's created per-test. Change constructor to not require `IClassFixture`-injected factory. The derived test class will create and dispose the factory in `InitializeAsync`/`DisposeAsync`. Alternatively, make the base class create the factory itself given a `HarnessSmokeSpec`. Keep `PlaywrightFixture` as `IClassFixture` (browser reuse is fine).
  **Files**: `tests/WeaveFleet.E2E/Infrastructure/HarnessSmokeTestBase.cs`
  **Acceptance**: Base class works with per-test factory lifecycle.

- [ ] 4. Rename/refactor `OpenCodeSmokeTests` → `HarnessSmokeTests` with `[Theory]`
  **What**: Replace `[HarnessSmokeFact]` with a `[HarnessSmokeTheory]` attribute (same skip logic). Use `[MemberData]` returning `HarnessSmokeSpec` rows. First row: OpenCode. The test method becomes `should_create_session_and_receive_assistant_response(HarnessSmokeSpec spec)`. Replace `AssertCreatedWithOpenCodeHarnessAsync` with `AssertCreatedWithHarnessAsync(spec.HarnessType)`. Create factory per invocation using the spec. Keep `IClassFixture<PlaywrightFixture>` for browser reuse.
  **Files**: `tests/WeaveFleet.E2E/Tests/HarnessSmokeTests.cs` (new, replaces `OpenCodeSmokeTests.cs`)
  **Acceptance**: Theory discovers one row (opencode); test passes end-to-end.

- [ ] 5. Create `HarnessSmokeTheoryAttribute`
  **What**: Like `HarnessSmokeFactAttribute` but extends `TheoryAttribute`. Same `FLEET_HARNESS_SMOKE` env-var skip logic. Move out of the test file into `Infrastructure/`.
  **Files**: `tests/WeaveFleet.E2E/Infrastructure/HarnessSmokeTheoryAttribute.cs`
  **Acceptance**: Theory is skipped when env var not set.

- [ ] 6. Delete `OpenCodeSmokeTests.cs`
  **What**: Remove the old file after `HarnessSmokeTests.cs` is verified working.
  **Files**: `tests/WeaveFleet.E2E/Tests/OpenCodeSmokeTests.cs` (delete)
  **Acceptance**: No compilation errors referencing old class.

- [ ] 7. Run tests and verify
  **What**: `dotnet build tests/WeaveFleet.E2E && FLEET_HARNESS_SMOKE=1 dotnet test tests/WeaveFleet.E2E --filter "Category=HarnessSmoke" -v n`
  **Acceptance**: OpenCode theory row passes; no other test regressions.

## Verification
- [ ] `dotnet build tests/WeaveFleet.E2E` compiles cleanly
- [ ] `dotnet test tests/WeaveFleet.E2E --filter "Category=HarnessSmoke"` skips when env var unset
- [ ] `FLEET_HARNESS_SMOKE=1 dotnet test tests/WeaveFleet.E2E --filter "Category=HarnessSmoke"` passes OpenCode row
- [ ] No regressions in `dotnet test tests/WeaveFleet.E2E --filter "Category!=HarnessSmoke"`

## Risks & Mitigations

| Risk | Mitigation |
|------|-----------|
| xUnit `[Theory]` with custom serializable types can cause discovery issues if `IXunitSerializable` is implemented incorrectly | Keep `HarnessSmokeSpec` simple; test discovery with `dotnet test --list-tests` before running |
| Per-test factory means slower tests (new Kestrel per row) | Acceptable for smoke tests; only a few rows expected |
| `[Theory]` rows that lack installed binaries will fail instead of skip | Add per-row skip logic: in `InitializeAsync`, check `registry.GetByType(spec.HarnessType)` availability and skip if unavailable |
| Filter expressions like `--filter "DisplayName~opencode"` may not work with custom serialization | Use `[MemberData]` with `TheoryData<HarnessSmokeSpec>` and override `ToString()` on the spec for readable display names |
