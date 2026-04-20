# E2E Test Suite Improvement Plan

## TL;DR
> **Summary**: Restructure E2E tests into smoke/workflow/security lanes, fix weak assertions, eliminate brittle patterns, and fill critical route coverage gaps — maximizing confidence per test minute.
> **Estimated Effort**: Large

## Context
### Original Request
Produce a prioritized improvement plan from the E2E audit findings: keep strong tests, fix weak ones, fill gaps, and structure the suite for maximum confidence per test minute.

### Key Findings
- **14 test files**, ~18 `[Fact]` methods across 2 base classes (`E2ETestBase` for no-auth, `AuthE2ETestBase` for OIDC)
- **13 frontend routes** exist but only 3 have E2E coverage: `/` (dashboard), `/sessions/$id`, `/analytics` (shallow)
- **10 uncovered routes**: `/settings`, `/board`, `/queue`, `/pipelines`, `/repositories`, `/templates`, `/welcome`, `/login`, `/github/$owner/$repo/pulls/$number`, `/github/$owner/$repo/issues/$number`, `/settings/plugins/$pluginId`
- `FleetDashboardTests.Dashboard_WithNoSessions_ShowsEmptyStateAndSummaryBar` does API-level cleanup of all sessions — brittle cross-test coupling
- `ErrorHandlingTests` uses `WaitForTimeoutAsync(2_000)` and weak OR-chained text assertions; spawn failure test doesn't assert error text is actually found
- `SessionLifecycleTests.DeleteSession_ShowsConfirmationDialog` cancels instead of completing delete — never verifies deletion works
- `SessionLifecycleTests.SessionDetail_ShowsTitleInHeader` asserts `header h2` is visible but never checks it contains "My Titled Session"
- `AnalyticsPageTests` only checks no-error + title not null — zero data assertions
- `Sidebar_ArchiveUnarchiveDelete_Flows` is a 50-line chained mega-test — one failure masks all subsequent assertions
- `SubAgentDelegationTests` is excellent but monolithic (230 lines); good candidate for extraction
- Page Object Model is well-established (`FleetDashboardPage`, `SessionDetailPage`, etc.) — extend, don't replace

---

## 1. Keep / Improve / Delete / Add Recommendations

### KEEP (high-value, well-structured)
- [x] `GoldenPathTests.CreateSession_SendPrompt_ReceivesStreamedResponse` — the smoke canary; keep as-is
- [x] `OidcSignInTests` — auth round-trip is critical
- [x] `OnboardingFlowTests` — onboarding completion happy path
- [x] `MessagePersistenceTests` — data integrity
- [x] `SessionMessageTests` — prompt/response core loop
- [x] `SkillPathTraversalSecurityTests` — security negative cases
- [x] `GitHubEndpointSecurityTests` — security negative cases
- [x] `SubAgentDelegationTests.DelegatedChildSession_StreamsLiveActivity_AndShowsBreadcrumb` — complex but valuable; keep
- [x] `ArchivedSessionDetail_DirectUrlIsReadableButNotWritableUntilUnarchived` — thorough archive test
- [x] `StoppedSession_CanBeArchivedUnarchivedAndResumed` — full lifecycle
- [x] `ArchivedSession_CanBePermanentlyDeletedFromArchivedView` — delete verification
- [x] `OnboardingRefreshRegressionTests` (all 3) — regression guards

### IMPROVE (fix assertions or structure)
- [x] **`Dashboard_WithNoSessions_ShowsEmptyStateAndSummaryBar`** — Replace API-level session cleanup with test isolation (dedicated factory or DB reset in `InitializeAsync`). The current loop-delete approach races with other tests sharing the factory.
  **Files**: `tests/WeaveFleet.E2E/Tests/FleetDashboardTests.cs`, `tests/WeaveFleet.E2E/Infrastructure/E2ETestBase.cs`
  **Acceptance**: Test passes without deleting other tests' sessions

- [x] **`Dashboard_SummaryBar_IsVisible`** — Assert summary bar contains expected stat labels (e.g., "Active", "Total"), not just visibility.
  **Files**: `tests/WeaveFleet.E2E/Tests/FleetDashboardTests.cs`
  **Acceptance**: Asserts at least one stat label text

- [x] **`SessionDetail_ShowsTitleInHeader`** — Assert header contains "My Titled Session", not just that `header h2` exists.
  **Files**: `tests/WeaveFleet.E2E/Tests/SessionLifecycleTests.cs`
  **Acceptance**: `Expect(header).ToContainTextAsync("My Titled Session")`

- [x] **`DeleteSession_ShowsConfirmationDialog`** — Complete the delete flow: click confirm, verify session card disappears from dashboard. Current test only cancels.
  **Files**: `tests/WeaveFleet.E2E/Tests/SessionLifecycleTests.cs`
  **Acceptance**: After confirm, `GetSessionCard(sessionId)` has count 0

- [x] **`SpawnFailure_ShowsErrorInDialog`** — Replace `WaitForTimeoutAsync(2_000)` with `WaitForSelectorAsync` on a specific error test-id. Replace OR-chained text match with `Expect(Page.GetByTestId("error-message")).ToBeVisibleAsync()`. If no `data-testid` exists on the error element, add one to the Vue component.
  **Files**: `tests/WeaveFleet.E2E/Tests/ErrorHandlingTests.cs`
  **Acceptance**: No `WaitForTimeoutAsync`; asserts specific error element visible

- [x] **`SendPromptFailure_ShowsErrorState`** — Same: replace timeout with explicit error element wait. Assert error text content, not just that prompt input is still visible.
  **Files**: `tests/WeaveFleet.E2E/Tests/ErrorHandlingTests.cs`
  **Acceptance**: Asserts error message text contains failure reason

- [x] **`Sidebar_ArchiveUnarchiveDelete_Flows`** — Split into 3 focused tests: (1) sidebar archive hides from active, (2) sidebar unarchive restores to active, (3) sidebar permanent delete removes entirely. Each test creates its own session.
  **Files**: `tests/WeaveFleet.E2E/Tests/SessionLifecycleTests.cs`
  **Acceptance**: 3 independent tests, each < 25 lines of test body

- [x] **`AnalyticsPage_LoadsWithoutErrors`** — After page load, assert at least one chart container or data element is rendered (not just "no error").
  **Files**: `tests/WeaveFleet.E2E/Tests/AnalyticsPageTests.cs`
  **Acceptance**: Asserts a chart/data container element exists

### DELETE (redundant or zero-value)
- [x] **`Dashboard_SummaryBar_IsVisible`** can be merged into `Dashboard_WithNoSessions_ShowsEmptyStateAndSummaryBar` once improved — it's a strict subset. Remove as standalone test.
  **Files**: `tests/WeaveFleet.E2E/Tests/FleetDashboardTests.cs`

### ADD (new tests — see Section 3 for phasing)
Listed below in priority order by confidence-per-minute impact.

---

## 2. Test Pyramid / Lanes

Structure tests into 3 lanes via `[Trait]` tags, run in CI in this order:

| Lane | Trait | Purpose | Target Duration | Run When |
|------|-------|---------|----------------|----------|
| **Smoke** | `[Trait("Lane", "Smoke")]` | Can the app boot, render, and complete the golden path? | < 30s | Every commit |
| **Workflow** | `[Trait("Lane", "Workflow")]` | Happy-path flows for each major feature area | < 3min | Every PR |
| **Security/Regression** | `[Trait("Lane", "Regression")]` | Edge cases, security negatives, refresh regressions, error handling | < 5min | Nightly + release |

### Smoke Lane (gate all PRs)
- `GoldenPathTests.CreateSession_SendPrompt_ReceivesStreamedResponse`
- New: `RouteSmoke_AllRoutesReturn200` (see below)
- `OidcSignInTests` (auth smoke)

### Workflow Lane
- All `SessionLifecycleTests` (improved)
- `FleetDashboardTests` (improved)
- `OnboardingFlowTests`
- `MessagePersistenceTests`
- `SessionMessageTests`
- `SubAgentDelegationTests`
- New route-specific workflow tests

### Security/Regression Lane
- `SkillPathTraversalSecurityTests`
- `GitHubEndpointSecurityTests`
- `OnboardingRefreshRegressionTests`
- `ErrorHandlingTests` (improved)
- New: 404/unauthorized navigation tests
- `UiResponsivenessBenchmarkTests`

---

## 3. Execution Phases

### Phase 1: Fix & Harden (1-2 days)
> Goal: Every existing test earns its keep. No timeouts, no weak assertions, no cross-test coupling.

- [x] 1. **Fix dashboard cleanup brittleness**
  **What**: Replace API-loop session deletion in `Dashboard_WithNoSessions` with a per-test DB reset or dedicated `IClassFixture` that guarantees empty state.
  **Files**: `tests/WeaveFleet.E2E/Tests/FleetDashboardTests.cs`, `tests/WeaveFleet.E2E/Infrastructure/E2ETestBase.cs`
  **Acceptance**: Test passes in parallel with other dashboard tests

- [x] 2. **Strengthen 5 weak assertions**
  **What**: Fix `SessionDetail_ShowsTitleInHeader` (assert text), `DeleteSession_ShowsConfirmationDialog` (complete delete), `SpawnFailure_ShowsErrorInDialog` (specific error selector), `SendPromptFailure_ShowsErrorState` (error text), `AnalyticsPage_LoadsWithoutErrors` (chart element). Add `data-testid="spawn-error-message"` to the Vue new-session dialog error display if missing.
  **Files**: `tests/WeaveFleet.E2E/Tests/SessionLifecycleTests.cs`, `tests/WeaveFleet.E2E/Tests/ErrorHandlingTests.cs`, `tests/WeaveFleet.E2E/Tests/AnalyticsPageTests.cs`
  **Acceptance**: Zero `WaitForTimeoutAsync` calls in error tests; all assertions check content not just visibility

- [x] 3. **Split sidebar mega-test**
  **What**: Break `Sidebar_ArchiveUnarchiveDelete_Flows` into 3 independent tests.
  **Files**: `tests/WeaveFleet.E2E/Tests/SessionLifecycleTests.cs`
  **Acceptance**: 3 tests, each independently passable

- [x] 4. **Merge redundant summary bar test**
  **What**: Delete `Dashboard_SummaryBar_IsVisible`, fold its assertion into the improved empty-state test.
  **Files**: `tests/WeaveFleet.E2E/Tests/FleetDashboardTests.cs`
  **Acceptance**: One fewer test, same coverage

- [x] 5. **Add `[Trait("Lane", "...")]` to all tests**
  **What**: Tag every test with Smoke/Workflow/Regression lane. Update CI to filter by lane.
  **Files**: All files in `tests/WeaveFleet.E2E/Tests/`
  **Acceptance**: `dotnet test --filter "Lane=Smoke"` runs exactly the smoke subset

### Phase 2: Route Coverage Blitz (2-3 days)
> Goal: Every frontend route has at least a smoke-level "loads without error" test, and key routes get workflow tests.

- [x] 6. **Add `RouteSmoke` parameterized test**
  **What**: Single `[Theory]` with `[InlineData("/settings")]`, `[InlineData("/board")]`, `[InlineData("/queue")]`, `[InlineData("/pipelines")]`, `[InlineData("/repositories")]`, `[InlineData("/templates")]`. Each navigates, asserts no JS errors and page renders a known container element. Tag as Smoke lane.
  **Files**: `tests/WeaveFleet.E2E/Tests/RouteSmokeTests.cs` (new)
  **Acceptance**: All 6 routes pass; any new route added to router triggers a test gap alert

- [x] 7. **Add Settings page workflow test**
  **What**: Navigate to `/settings`, verify settings form renders, change a value, save, reload, verify persistence. Create `SettingsPage` POM.
  **Files**: `tests/WeaveFleet.E2E/Tests/SettingsPageTests.cs` (new), `tests/WeaveFleet.E2E/Pages/SettingsPage.cs` (new)
  **Acceptance**: Settings round-trip verified

- [x] 8. **Add GitHub detail page smoke tests**
  **What**: Seed a GitHub issue/PR via API, navigate to `/github/$owner/$repo/issues/$number` and `/github/$owner/$repo/pulls/$number`, assert page renders with title. Create `GitHubDetailPage` POM.
  **Files**: `tests/WeaveFleet.E2E/Tests/GitHubDetailPageTests.cs` (new), `tests/WeaveFleet.E2E/Pages/GitHubDetailPage.cs` (new)
  **Acceptance**: Both GitHub detail routes render seeded data

- [x] 9. **Add 404/unauthorized navigation tests**
  **What**: Navigate to `/sessions/nonexistent-id`, assert 404 or "not found" UI. Navigate to protected route without auth (if applicable), assert redirect to login.
  **Files**: `tests/WeaveFleet.E2E/Tests/NavigationErrorTests.cs` (new)
  **Acceptance**: 404 page renders; unauthorized redirects to `/login`

### Phase 3: Resilience & Edge Cases (2-3 days)
> Goal: Cover the failure modes that cause real production incidents.

- [x] 10. **WebSocket disconnect/reconnect test**
  **What**: During an active session, simulate network interruption (close WS via CDP or intercept), verify UI shows disconnected state, then restore and verify reconnection + message catch-up.
  **Files**: `tests/WeaveFleet.E2E/Tests/WebSocketResilienceTests.cs` (new)
  **Acceptance**: Disconnect indicator appears; after reconnect, messages resume without page reload

- [x] 11. **Concurrent prompt behavior test**
  **What**: Send a prompt while session is busy (previous prompt still processing). Verify UI either queues, disables input, or shows appropriate feedback — no silent drop.
  **Files**: `tests/WeaveFleet.E2E/Tests/SessionMessageTests.cs` (extend existing)
  **Acceptance**: Second prompt either queued or rejected with visible feedback

- [x] 12. **Skill CRUD happy path**
  **What**: If skills UI exists, test create → list → edit → delete flow. If skills are API-only, add API-level integration test.
  **Files**: `tests/WeaveFleet.E2E/Tests/SkillCrudTests.cs` (new)
  **Acceptance**: Full CRUD cycle completes

- [x] 13. **Analytics data assertions**
  **What**: Seed sessions with known activity, navigate to `/analytics`, assert charts/tables contain expected aggregated values (not just "page loads").
  **Files**: `tests/WeaveFleet.E2E/Tests/AnalyticsPageTests.cs` (extend)
  **Acceptance**: At least one numeric assertion on rendered analytics data

---

## 4. Specific Test Rewrite Examples

### Example A: Fix `SessionDetail_ShowsTitleInHeader`

**Before** (asserts element exists, not content):
```csharp
var header = Page.Locator("header h2");
await Microsoft.Playwright.Assertions.Expect(header).ToBeVisibleAsync();
```

**After** (asserts actual title text):
```csharp
var header = Page.Locator("header h2");
await Microsoft.Playwright.Assertions.Expect(header).ToContainTextAsync("My Titled Session");
```

### Example B: Fix `SpawnFailure_ShowsErrorInDialog`

**Before** (timeout + OR-chained text, never asserted):
```csharp
await Page.WaitForTimeoutAsync(2_000);
var errorLocator = Page.GetByText("error", ...).Or(Page.GetByText("failed", ...)).Or(...);
// errorLocator is never awaited/asserted!
await Microsoft.Playwright.Assertions.Expect(Page.GetByTestId("new-session-button")).ToBeVisibleAsync();
```

**After** (explicit error element wait):
```csharp
// Requires data-testid="spawn-error-message" on the error display in NewSessionDialog.vue
var errorMessage = Page.GetByTestId("spawn-error-message");
await Microsoft.Playwright.Assertions.Expect(errorMessage).ToBeVisibleAsync(
    new() { Timeout = 5_000 });
await Microsoft.Playwright.Assertions.Expect(errorMessage).ToContainTextAsync("spawn");
Page.Url.ShouldNotContain("/sessions/");
```

### Example C: Split `Sidebar_ArchiveUnarchiveDelete_Flows`

**Before** (one 50-line chained test):
```csharp
[Fact]
public async Task Sidebar_ArchiveUnarchiveDelete_Flows()
{
    // ... create session, stop, archive via sidebar, switch filter,
    // unarchive via sidebar, switch filter, delete via sidebar — all in one
}
```

**After** (3 focused tests):
```csharp
[Fact]
[Trait("Lane", "Workflow")]
public async Task Sidebar_Archive_HidesSessionFromActiveView() { /* create, stop, archive via sidebar, assert hidden */ }

[Fact]
[Trait("Lane", "Workflow")]
public async Task Sidebar_Unarchive_RestoresSessionToActiveView() { /* create, stop, archive, unarchive via sidebar, assert visible */ }

[Fact]
[Trait("Lane", "Workflow")]
public async Task Sidebar_PermanentDelete_RemovesSessionEntirely() { /* create, stop, delete via sidebar, assert gone from All filter */ }
```

### Example D: Route Smoke `[Theory]`

```csharp
[Theory]
[Trait("Lane", "Smoke")]
[InlineData("/settings")]
[InlineData("/board")]
[InlineData("/queue")]
[InlineData("/pipelines")]
[InlineData("/repositories")]
[InlineData("/templates")]
public async Task Route_LoadsWithoutJsErrors(string route)
{
    var jsErrors = new List<string>();
    Page.PageError += (_, msg) => jsErrors.Add(msg);

    await Page.GotoAsync(route);
    await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

    jsErrors.ShouldBeEmpty($"JS errors on {route}: {string.Join("; ", jsErrors)}");
    var main = Page.Locator("main");
    await Microsoft.Playwright.Assertions.Expect(main).ToBeVisibleAsync();
}
```

---

## Verification
- [x] `dotnet test --filter "Lane=Smoke"` passes in < 30s
- [x] `dotnet test --filter "Lane=Workflow"` passes in < 3min
- [x] `dotnet test --filter "Category=E2E"` (full suite) passes in < 8min
- [x] Zero `WaitForTimeoutAsync` calls remain in non-benchmark tests
- [x] Every route in `routeTree.gen.ts` has at least smoke coverage
- [x] No test depends on cleanup of another test's data
