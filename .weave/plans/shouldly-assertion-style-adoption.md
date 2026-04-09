# Adopt Shouldly as the Preferred .NET Test Assertion Style

## TL;DR
> **Summary**: Introduce Shouldly via central package management, make it available across all six .NET test projects, and migrate xUnit `Assert` usage in phased waves instead of a single repo-wide rewrite. New and touched tests should use Shouldly by default, while existing assertions are converted incrementally with NSubstitute interaction assertions left unchanged.
> **Estimated Effort**: Large

## Context

### Original Request
Create an implementation plan for adopting Shouldly as the preferred assertion style in this repository's .NET test projects. Base it on this discovered context: six test projects exist (Api, Application, Domain, Infrastructure, TestHarness, E2E); current assertions are overwhelmingly xUnit Assert with no existing Shouldly usage; central package management is enabled via `Directory.Packages.props`; migration scope is about 892 Assert call sites with `Infrastructure.Tests` the largest by far; NSubstitute interaction assertions should remain unchanged.

### Key Findings
- Six .NET test projects are present:
  - `tests/WeaveFleet.Api.Tests/WeaveFleet.Api.Tests.csproj`
  - `tests/WeaveFleet.Application.Tests/WeaveFleet.Application.Tests.csproj`
  - `tests/WeaveFleet.Domain.Tests/WeaveFleet.Domain.Tests.csproj`
  - `tests/WeaveFleet.Infrastructure.Tests/WeaveFleet.Infrastructure.Tests.csproj`
  - `tests/WeaveFleet.TestHarness/WeaveFleet.TestHarness.csproj`
  - `tests/WeaveFleet.E2E/WeaveFleet.E2E.csproj`
- Central package management is enabled in `Directory.Packages.props`, so Shouldly versioning belongs there.
- There is currently no Shouldly usage anywhere in the repository.
- xUnit `Assert` usage is widespread (~892 call sites), with `WeaveFleet.Infrastructure.Tests` accounting for the largest share.
- Test projects already rely on shared build configuration via `tests/Directory.Build.props`, which is the best touchpoint for any cross-project test-only conventions such as shared usings.
- NSubstitute is already used for collaboration verification in multiple test suites; those `Received()` / `DidNotReceive()` assertions should stay as-is.
- E2E verification already has documented commands in `tests/WeaveFleet.E2E/README.md`, so rollout verification should keep unit/integration and E2E runs separate.

## Objectives

### Core Objective
Adopt Shouldly as the preferred assertion style for all .NET test projects without changing test behavior, while rolling the migration out incrementally so the repository stays reviewable and low-risk.

### Deliverables
- [x] Shouldly is versioned centrally and referenced by all six .NET test projects.
- [x] Shared test configuration makes Shouldly easy to use in any test project.
- [x] A documented assertion-style policy states that new and touched tests should use Shouldly, while legacy xUnit `Assert` usage is migrated incrementally.
- [x] Existing xUnit `Assert` call sites are reduced through phased conversion, prioritizing smaller projects first and `WeaveFleet.Infrastructure.Tests` in focused slices.
- [x] NSubstitute interaction assertions remain unchanged throughout the migration.

### Definition of Done
- [x] `dotnet test --filter "Category!=E2E"` passes after the migration work.
- [x] `dotnet test tests/WeaveFleet.E2E/ --filter "Category=E2E"` passes after the migration work.
- [x] All six test projects can compile and use Shouldly assertions without adding ad hoc package versions.
- [x] Search results show that new or touched tests use Shouldly assertions by default, with any remaining xUnit `Assert` usage limited to untouched backlog or explicitly documented exceptions.

### Guardrails (Must NOT)
- Do NOT change production code to accommodate assertion-style migration.
- Do NOT rewrite NSubstitute interaction assertions such as `Received()` / `DidNotReceive()`.
- Do NOT attempt a single massive 892-site conversion in one PR.
- Do NOT replace xUnit as the test framework; this is an assertion-style migration only.

### Scope
- In scope: assertion usage in the six .NET test projects, shared test-project configuration, and contributor guidance for preferred assertion style.
- In scope: mechanical conversion of xUnit `Assert` usages where Shouldly provides a clear equivalent.
- Out of scope: production-source changes, mocking framework changes, and non-.NET test stacks.

### Non-Goals
- Full elimination of every xUnit `Assert` in the first rollout.
- Rewriting helper APIs or production objects solely to make assertions look more fluent.
- Converting NSubstitute collaboration checks into any alternate style.
- Introducing custom analyzers/code fixes in the first pass unless the manual migration proves too hard to sustain.

### Design Decisions
- Add `Shouldly` to `Directory.Packages.props` and reference it from each of the six test `.csproj` files.
- Prefer a shared test-level import in `tests/Directory.Build.props` so test files can use Shouldly without repetitive `using Shouldly;` statements.
- Keep xUnit for `[Fact]`, `[Theory]`, fixtures, and exception-hosting semantics; only assertion syntax changes.
- Use actual-first Shouldly idioms consistently (for example: `actual.ShouldBe(expected)`, `value.ShouldNotBeNull()`, `items.ShouldHaveSingleItem()`, `obj.ShouldBeOfType<T>()`).
- Preserve NSubstitute interaction assertions exactly as they are; only surrounding state/value assertions move to Shouldly.
- Roll out in phases: establish dependency + policy, migrate low-risk projects, then tackle `WeaveFleet.Infrastructure.Tests` in bounded slices.

### Recommended Wording
> New tests and any existing tests touched for feature work or bug fixes should use Shouldly assertions by default. Existing xUnit `Assert`-based tests do not need to be rewritten wholesale; migrate them incrementally as files are touched or as part of focused assertion-style cleanup. NSubstitute interaction assertions such as `Received()` and `DidNotReceive()` should remain unchanged.

### Concrete File Touchpoints
- `Directory.Packages.props`
- `tests/Directory.Build.props`
- `tests/WeaveFleet.Api.Tests/WeaveFleet.Api.Tests.csproj`
- `tests/WeaveFleet.Application.Tests/WeaveFleet.Application.Tests.csproj`
- `tests/WeaveFleet.Domain.Tests/WeaveFleet.Domain.Tests.csproj`
- `tests/WeaveFleet.Infrastructure.Tests/WeaveFleet.Infrastructure.Tests.csproj`
- `tests/WeaveFleet.TestHarness/WeaveFleet.TestHarness.csproj`
- `tests/WeaveFleet.E2E/WeaveFleet.E2E.csproj`
- `tests/README.md` (new, recommended location for shared .NET test conventions)
- Representative migration hotspots:
  - `tests/WeaveFleet.Infrastructure.Tests/Harnesses/OpenCode/OpenCodeMapperTests.cs`
  - `tests/WeaveFleet.Infrastructure.Tests/Harnesses/OpenCode/OpenCodeHarnessInstancePersistenceTests.cs`
  - `tests/WeaveFleet.Infrastructure.Tests/Services/HarnessEventRelayTests.cs`
  - `tests/WeaveFleet.Application.Tests/Services/SessionOrchestratorTests.cs`
  - `tests/WeaveFleet.E2E/Tests/SubAgentDelegationTests.cs`

### Risks
- Mechanical conversions can subtly change semantics when moving from xUnit helpers like `Assert.Single`, `Assert.IsType`, or `Assert.ThrowsAsync` to Shouldly equivalents.
- `WeaveFleet.Infrastructure.Tests` is large enough that unbounded migration will create noisy diffs and merge conflicts.
- Some assertions may read worse if converted naively; the migration should favor clarity over strict one-to-one replacement.
- E2E verification is slower and depends on the documented frontend build/setup flow, so it should remain a late verification gate rather than an every-file migration loop.

## TODOs

- [x] 1. Enable Shouldly in shared test infrastructure
  **What**: Add the central `Shouldly` package version, reference it from all six .NET test projects, and expose the namespace through shared test configuration so new tests can adopt it with minimal friction.
  **Files**: `Directory.Packages.props`, `tests/Directory.Build.props`, `tests/WeaveFleet.Api.Tests/WeaveFleet.Api.Tests.csproj`, `tests/WeaveFleet.Application.Tests/WeaveFleet.Application.Tests.csproj`, `tests/WeaveFleet.Domain.Tests/WeaveFleet.Domain.Tests.csproj`, `tests/WeaveFleet.Infrastructure.Tests/WeaveFleet.Infrastructure.Tests.csproj`, `tests/WeaveFleet.TestHarness/WeaveFleet.TestHarness.csproj`, `tests/WeaveFleet.E2E/WeaveFleet.E2E.csproj`
  **Acceptance**: Each test project restores and builds with Shouldly available; no project hard-codes its own Shouldly version.

- [x] 2. Document the assertion policy and conversion rules
  **What**: Add a shared testing-conventions document that states the preferred style, the incremental migration policy, the explicit exception for NSubstitute interaction assertions, and a small mapping guide for common xUnit-to-Shouldly conversions.
  **Files**: `tests/README.md` (new), `tests/WeaveFleet.E2E/README.md`
  **Acceptance**: The repository has a single place contributors can cite for assertion-style expectations, including the recommended wording for new/touched tests and examples for common conversions.

- [x] 3. Pilot the migration in smaller/lower-risk test projects
  **What**: Convert xUnit `Assert` usage in the smaller projects first to settle idioms, confirm shared imports work, and establish review patterns before the large Infrastructure sweep.
  **Files**: `tests/WeaveFleet.Domain.Tests`, `tests/WeaveFleet.Api.Tests`, `tests/WeaveFleet.Application.Tests`, `tests/WeaveFleet.TestHarness`
  **Acceptance**: These projects primarily use Shouldly for state/value assertions, continue passing their targeted tests, and preserve all existing NSubstitute interaction checks.

- [x] 4. Migrate `WeaveFleet.Infrastructure.Tests` in bounded slices
  **What**: Tackle the largest project by area rather than by repo-wide search/replace. Recommended slices: `Harnesses/OpenCode`, `Harnesses/ClaudeCode`, `Services`, `Analytics`, and `Data`. Convert xUnit value/type/null/collection assertions to Shouldly while leaving NSubstitute interactions alone.
  **Files**: `tests/WeaveFleet.Infrastructure.Tests/Harnesses/OpenCode`, `tests/WeaveFleet.Infrastructure.Tests/Harnesses/ClaudeCode`, `tests/WeaveFleet.Infrastructure.Tests/Services`, `tests/WeaveFleet.Infrastructure.Tests/Analytics`, `tests/WeaveFleet.Infrastructure.Tests/Data`
  **Acceptance**: Each slice lands in a reviewable change set, targeted test runs stay green, and the highest-volume xUnit `Assert` hotspots are reduced without broad semantic churn.

- [x] 5. Finish E2E migration and sweep for residual policy drift
  **What**: Convert remaining xUnit `Assert` usage in `WeaveFleet.E2E`, verify the policy is being followed in touched tests, and do a final search for any accidental conversion of NSubstitute interaction assertions or newly added xUnit-style assertions in migrated areas.
  **Files**: `tests/WeaveFleet.E2E/Tests`, `tests/WeaveFleet.E2E/README.md`
  **Acceptance**: E2E tests continue to pass, migrated files consistently use Shouldly for assertions, and interaction-verification patterns remain NSubstitute-native.

- [x] 6. Run full verification and record residual backlog
  **What**: Run the standard non-E2E and E2E test commands, then capture any intentionally deferred xUnit `Assert` residues as explicit follow-up work instead of leaving the migration state ambiguous.
  **Acceptance**: `dotnet test --filter "Category!=E2E"` passes, `dotnet test tests/WeaveFleet.E2E/ --filter "Category=E2E"` passes, and remaining xUnit `Assert` usage is understood as backlog rather than accidental drift.

## Verification
- [x] All tests pass
- [x] No regressions
- [x] `dotnet test --filter "Category!=E2E"`
- [x] `dotnet test tests/WeaveFleet.E2E/ --filter "Category=E2E"`
- [x] Search confirms `Shouldly` is referenced by all six test projects
- [x] Search confirms new/touched migrated tests prefer Shouldly while `Received()` / `DidNotReceive()` interaction assertions remain unchanged
