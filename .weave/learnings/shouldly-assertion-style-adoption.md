# Learnings: shouldly-assertion-style-adoption

## Task 1: Enable Shouldly in shared test infrastructure
- **Discrepancy**: Startup validation warned that several referenced test project files did not exist, but all six test project `.csproj` files and `tests/Directory.Build.props` were already present in the repository.
- **Resolution**: Updated the existing central package and test project files in place, then verified the full solution builds cleanly in release mode.
- **Suggestion**: Re-run file-reference validation against the current workspace before emitting missing-file warnings for pre-existing test assets.

## Task 2: Document the assertion policy and conversion rules
- **Discrepancy**: The plan referenced `tests/README.md` as a new recommended shared conventions document, but no such file existed yet.
- **Resolution**: Created `tests/README.md` as the canonical policy and mapping guide, and added a concise Shouldly-specific assertion section to the existing E2E README.
- **Suggestion**: Mark `tests/README.md` explicitly as a new file in the plan metadata to reduce ambiguity during execution.

## Task 3: Pilot the migration in smaller/lower-risk test projects
- **Discrepancy**: The plan scoped four projects as broad directory-level migrations, but the actual xUnit `Assert` usage in those projects was concentrated in a smaller set of existing test files rather than spread uniformly across each tree.
- **Resolution**: Converted the concrete assertion-bearing files in Domain, Api, Application, and TestHarness to Shouldly while preserving NSubstitute interaction assertions and validating each project with targeted release-mode test runs.
- **Suggestion**: For broad migration tasks, list the current assertion-bearing files or hotspots up front so the execution scope is explicit and easier to verify.

## Task 4: Migrate `WeaveFleet.Infrastructure.Tests` in bounded slices
- **Discrepancy**: The recommended Infrastructure slices covered most of the high-volume areas, but a final repo-level verification still surfaced additional `Assert` usage in `HarnessRegistryTests` and the infrastructure smoke test outside the named subdirectories.
- **Resolution**: Converted the planned OpenCode, ClaudeCode, Services, Analytics, and Data slices first, then finished the remaining Infrastructure test files uncovered by full-scope verification so the project ended with zero `Assert.` usage and a clean release build/test pass.
- **Suggestion**: For large phased migrations, include an explicit "repo-level cleanup within project scope" step in the task wording so residual files outside named hotspots are expected rather than discovered late.

## Task 5: Finish E2E migration and sweep for residual policy drift
- **Discrepancy**: The remaining E2E `Assert` usage was limited to a small set of tests rather than spread across the full suite, so the main work was a cleanup pass plus verification that the suite still behaved the same.
- **Resolution**: Converted the remaining E2E assertions to Shouldly, confirmed no `Assert.` usage remains under `tests/WeaveFleet.E2E`, and validated the full E2E suite in release mode with the `Category=E2E` filter.
- **Suggestion**: When a final migration phase is mostly cleanup, call out that it is expected to be a narrow residual sweep to make completion criteria easier to inspect.

## Task 6: Run full verification and record residual backlog
- **Discrepancy**: Running the non-E2E and E2E verification commands in parallel caused a transient `.deps.json` file lock in the shared E2E build output, even though each command passed independently.
- **Resolution**: Re-ran the standard verification commands sequentially, confirmed both pass, verified all six test projects reference Shouldly, confirmed there is no remaining `Assert.` usage under `tests`, and observed that NSubstitute `Received()` / `DidNotReceive()` assertions remain in place.
- **Suggestion**: For overlapping .NET test commands that build shared outputs, prefer sequential verification to avoid false-negative file-lock failures.
