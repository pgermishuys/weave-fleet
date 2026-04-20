# Learnings: E2E Test Suite Improvement Plan

## Task 13: Fix dashboard cleanup brittleness
- **Discrepancy**: The plan assumed the fix might require changes in `tests/WeaveFleet.E2E/Infrastructure/E2ETestBase.cs`, but the cleanest isolation point was the `FleetDashboardTests` class itself.
- **Resolution**: Used a dedicated `FleetWebApplicationFactory` per `FleetDashboardTests` instance, removed the delete-all `/api/sessions` cleanup loop, and kept `E2ETestBase` unchanged.
- **Suggestion**: Call out that per-test factory isolation is acceptable when base fixture changes would broaden scope unnecessarily.

## Task 13: Fix dashboard cleanup brittleness
- **Discrepancy**: Verification exposed an unrelated selector ambiguity in `NewSessionDialog.SetDirectoryAsync`; the plan mentioned test isolation but not this page-object collision.
- **Resolution**: Changed the directory locator to target the dialog textbox role explicitly so tests no longer match both the radio and textbox labeled "Directory".
- **Suggestion**: Note incidental shared test-infrastructure fixes when they block or distort verification of the planned task.

## Task 14: Strengthen 5 weak assertions
- **Discrepancy**: The plan scoped changes to E2E test files, but reliable error assertions also required surfacing deterministic error text/test IDs from the Vue UI and composables.
- **Resolution**: Added explicit `data-testid` hooks plus improved error parsing/display in the session creation and prompt sending UI so E2E tests can assert real user-visible failures.
- **Suggestion**: When a plan asks for stronger UI assertions, include the relevant frontend component/composable files if testability hooks or error surfacing are missing.

## Task 15: Split sidebar mega-test
- **Discrepancy**: The plan focused on splitting one long test, but the first implementation reused a stop flow that was unreliable from the detail page in this environment.
- **Resolution**: Kept the split tests but changed stopped-session setup to use the fleet dashboard/card termination path, matching the more stable existing sidebar workflow pattern.
- **Suggestion**: When decomposing lifecycle tests, call out any preferred preparation path (detail page vs dashboard/sidebar) if one is already known to be more reliable.

## Task 16: Merge redundant summary bar test
- **Discrepancy**: The first verification attempt used the full E2E suite, which obscured this narrow task behind unrelated failures.
- **Resolution**: Verified the change with focused release-mode build and `FleetDashboardTests` execution only, matching the actual file and behavior touched.
- **Suggestion**: For single-file cleanup tasks, specify focused verification scope explicitly so unrelated suite instability does not distort acceptance.

## Task 17: Add lane traits to all tests
- **Discrepancy**: The lane-tagging work itself was correct, but smoke-lane execution exposed a missing shared UI test hook rather than a trait-mapping problem.
- **Resolution**: Kept the lane traits and restored the existing expected `session-status-indicator` test id in `SessionDetailHeader.vue` so smoke tests could pass without changing the intended lane subset.
- **Suggestion**: When reorganizing test filters/lanes, account for shared page-object assumptions that may only surface once a specific subset starts running in isolation.

## Task 18: Add RouteSmoke parameterized test
- **Discrepancy**: The `/settings` route smoke check surfaced an unrelated existing backend failure on `GET /api/skills`, which would have made the new route smoke test fail for reasons outside route rendering.
- **Resolution**: Stubbed `/api/skills` within the smoke test for `/settings` only so the route smoke test verifies page load/rendering without being distorted by that separate backend issue.
- **Suggestion**: For route smoke coverage, identify and neutralize non-route-specific dependency failures in-test when the goal is page boot/render confidence rather than backend feature validation.

## Task 19: Add Settings page workflow test
- **Discrepancy**: The settings workflow required persistence verification, but the page did not expose an obvious explicit save action for workspace preferences and still hit the unrelated `/api/skills` failure.
- **Resolution**: Added a focused settings page object, verified persistence through a real credential save plus workspace label reload check, and scoped the `/api/skills` stub to this test so the workflow behavior could be validated cleanly.
- **Suggestion**: When planning settings workflows, note whether persistence is implicit/reactive vs button-driven and call out unrelated page dependencies that may need scoped stubbing.

## Task 20: Add GitHub detail page smoke tests
- **Discrepancy**: The plan described “seeding” GitHub detail data, but these routes depend on external-style integration fetches rather than local persisted entities.
- **Resolution**: Seeded the route with narrowly scoped Playwright API stubs for issue/PR detail and comment endpoints, then verified the rendered title and body through a dedicated page object.
- **Suggestion**: For integration-backed detail pages, specify whether seeding should happen via DB state, app API, or request stubbing so the intended test boundary is explicit.

## Task 21: Add 404/unauthorized navigation tests
- **Discrepancy**: The nonexistent session route did not present a dedicated not-found page in the SPA as the plan implied; it kept the session shell mounted while the backend returned 404.
- **Resolution**: Made the route boundary explicit by asserting the real `GET /api/sessions/nonexistent-id` 404 response and not-found payload, while separately verifying auth-enabled protected-route redirect behavior through `/login` with preserved `returnUrl`.
- **Suggestion**: Distinguish between route-level 404 UI expectations and backend-detail-fetch 404 behavior when planning navigation error tests for SPA detail pages.

## Task 22: Add WebSocket disconnect/reconnect test
- **Discrepancy**: The plan assumed an existing disconnected-state indicator, but the app did not expose a clear user-visible websocket disconnect surface suitable for E2E assertions.
- **Resolution**: Added a narrow websocket suspend/resume test hook plus explicit disconnected indicator/banner behavior in the existing session detail UI, then verified catch-up without page reload.
- **Suggestion**: When planning realtime resilience tests, note whether the product already exposes observable disconnect/reconnect UI; if not, include minimal UX/testability updates in scope.

## Task 23: Add concurrent prompt behavior test
- **Discrepancy**: The plan framed this as queue-or-reject, but the actual product behavior is explicit queueing with a visible badge rather than disabling or rejecting the second prompt.
- **Resolution**: Added the test against the real queueing behavior, asserting visible `1 queued` feedback, eventual delivery of the second prompt, and queue badge disappearance after processing.
- **Suggestion**: When planning concurrency tests, describe the expected current UX if known so the test can validate intended behavior instead of exploring multiple mutually exclusive outcomes.

## Task 24: Skill CRUD happy path
- **Discrepancy**: The plan assumed a usable skills CRUD UI might exist, but the current skills surface is only partial UI (list/install/remove), has no edit flow, and its install contract does not match the backend API.
- **Resolution**: Added a reliable backend lifecycle test that covers create, list, edit-via-installed-prompt update, detail fetch, and delete; also removed an obsolete duplicate `/api/skills` stub that caused list requests to fail with 500.
- **Suggestion**: For skills coverage, distinguish between visual UI presence and actual end-to-end CRUD capability, and call out contract mismatches or legacy duplicate endpoints that must be resolved before UI CRUD can be tested.

## Task 25: Analytics data assertions
- **Discrepancy**: The plan framed analytics as a UI assertion upgrade, but the route is backed by real analytics database queries and a default recent-date filter that can hide seeded historical data.
- **Resolution**: Seeded the real analytics DB with deterministic in-range token/session data and asserted rendered overview values for tokens and sessions.
- **Suggestion**: For analytics E2E coverage, specify whether tests should seed the analytics DB directly and ensure seeded dates fall inside the page's default filter window unless the test explicitly changes filters.

## Task 26: Verification - Smoke lane
- **Discrepancy**: Running the lane filter at the solution level emits "No test matches the given testcase filter" messages from non-E2E test assemblies, which can look like failures even when the smoke lane passes.
- **Resolution**: Verified the release-mode smoke run by its actual E2E test results and wall-clock duration; the smoke subset passed in under 30 seconds without code changes.
- **Suggestion**: For lane verification steps, note that solution-level filtered runs may include harmless non-matching test-project noise, and judge acceptance by the targeted lane results and duration.

## Task 27: Verification - Workflow lane
- **Discrepancy**: Workflow-lane isolation exposed drift between the current session-detail UX and the test/page-object contract: some E2E test ids were missing, direct route loads were not setting the active session id, and action flows had shifted from app dialogs/links to native or immediate behaviors.
- **Resolution**: Restored the expected session-detail test hooks and deterministic action behavior, re-bound direct loads to the active session store entry, and verified the release-mode workflow lane passes in under 3 minutes.
- **Suggestion**: For workflow verification, expect route-level and page-object contracts to regress when UX components are refactored; preserve stable E2E hooks and active-session binding for direct deep-link scenarios.

## Task 28: Verification - full E2E suite
- **Discrepancy**: The plan treated full-suite verification as a pure validation step, but running the entire E2E assembly together exposed auth-host startup and migration races that lane-specific runs had not surfaced.
- **Resolution**: Disabled xUnit parallelization for the E2E assembly in `tests/WeaveFleet.E2E/AssemblyInfo.cs`, then re-ran the release build and full `Category=E2E` suite successfully in under 8 minutes.
- **Suggestion**: For full-suite verification tasks, account for suite-level shared-fixture concurrency issues that may only appear when all auth and non-auth E2E tests run together.

## Task 29: Verification - remove test timeouts
- **Discrepancy**: The plan framed timeout removal as mostly error-test cleanup, but remaining nondeterministic waits persisted in route smoke and onboarding refresh/flow tests.
- **Resolution**: Replaced those waits with network-idle navigation and explicit `summary-bar` visibility assertions, then verified the E2E project had no remaining `WaitForTimeoutAsync` usages and still passed in release mode.
- **Suggestion**: For timeout-elimination verification, search the whole E2E project rather than assuming only the originally weak tests still contain fixed delays.

## Task 30: Verification - route smoke coverage
- **Discrepancy**: The plan assumed prior route additions covered the entire generated router tree, but `/welcome`, `/login`, and `/settings/plugins/$pluginId` still lacked explicit smoke-level assertions.
- **Resolution**: Extended `RouteSmokeTests` with dedicated coverage for those routes, using scoped stubs where needed, and verified the full route-to-test map against `client/src/routeTree.gen.ts` in release mode.
- **Suggestion**: For router coverage tasks, compare the generated route tree directly against an explicit route-to-test matrix rather than inferring completeness from earlier feature tasks.

## Task 31: Verification - test data isolation
- **Discrepancy**: The final isolation check did not require new code changes, but it did reveal that proving isolation depended on an explicit audit of fixture scope, per-test identifiers, and onboarding resets rather than just rerunning the suite.
- **Resolution**: Audited the E2E suite for delete-all cleanup, shared persisted identifiers, and cross-test state assumptions; confirmed existing per-test factories, GUID-based data, and onboarding resets already eliminated cleanup coupling, then re-verified the full release-mode E2E suite.
- **Suggestion**: For data-isolation verification, require both a suite pass and a targeted audit of shared-state patterns so acceptance is evidenced even when no code changes are needed.
