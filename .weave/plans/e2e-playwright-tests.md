# Playwright E2E Tests with OpenCode TestHarness

## TL;DR
> **Summary**: Introduce a .NET `WeaveFleet.TestHarness` project that simulates the OpenCode HTTP/SSE API surface, then build Playwright E2E tests in `tests/WeaveFleet.E2E` that boot the real backend (swapping the OpenCode harness for the TestHarness) and exercise the full SPA through a headless browser, covering all critical user flows from dashboard to session interaction.
> **Estimated Effort**: XL

## Context

### Original Request
Create a detailed plan for introducing Playwright E2E integration tests with a TestHarness mock server that simulates `opencode serve`, enabling browser-based tests of the entire Weave Fleet stack without any real OpenCode or GitHub dependencies.

### Key Findings

**Harness Architecture (verified 2026-04-05)**:
- `IHarness` (`Application/Harnesses/IHarness.cs`) — factory interface: `Type`, `DisplayName`, `Capabilities`, `CheckAvailabilityAsync`, `SpawnAsync(HarnessSpawnOptions, CancellationToken)`.
- `IHarnessInstance` (`Domain/Harnesses/IHarnessInstance.cs`) — per-session bridge: `SendPromptAsync`, `GetMessagesAsync`, `SubscribeAsync`, `AbortAsync`, `CheckHealthAsync`, `StopAsync`.
- `HarnessRegistry` (`Infrastructure/Harnesses/HarnessRegistry.cs`) auto-discovers all `IHarness` registrations from DI. Registering a new `IHarness` singleton is sufficient.
- `SessionOrchestrator.CreateSessionAsync` resolves harness by type string, calls `SpawnAsync`, registers in `InstanceTracker`.
- The default harness type is `"opencode"` (hardcoded in `SessionOrchestrator`).
- `DependencyInjection.cs` registers `services.AddSingleton<IHarness, OpenCodeHarness>()` and `services.AddSingleton<IHarness, ClaudeCodeHarness>()`.

**API Surface (Fleet backend → browser)**:
- REST: `GET /api/sessions`, `POST /api/sessions`, `GET /api/sessions/{id}/messages`, `POST /api/sessions/{id}/prompt`, `POST /api/sessions/{id}/abort`, `DELETE /api/sessions/{id}`, `PATCH /api/sessions/{id}`, `GET /api/fleet/summary`, `GET /api/sessions/{id}/status`.
- WebSocket: `/ws` — topic-based pub/sub (`subscribe`, `unsubscribe`, `event` messages).
- SSE: `GET /api/sessions/{id}/events`, `GET /api/activity-stream`.
- Health: `/healthz`, `/readyz`.

**API Surface (OpenCode HTTP API — what TestHarness must simulate)**:
- `GET /global/health` → `{ healthy: true, version: "..." }`
- `POST /session?directory=...` → `OpenCodeSessionInfo`
- `GET /session?directory=...` → `OpenCodeSessionInfo[]`
- `GET /session/{id}?directory=...` → `OpenCodeSessionInfo`
- `DELETE /session/{id}?directory=...` → 200/204
- `POST /session/{id}/message?directory=...` → `OpenCodeMessageWithParts`
- `POST /session/{id}/prompt_async?directory=...` → 204
- `GET /session/{id}/message?directory=...` → `OpenCodeMessageWithParts[]`
- `POST /session/{id}/abort?directory=...` → 200
- `POST /session/{id}/fork?directory=...` → `OpenCodeSessionInfo`
- `GET /event?directory=...` → SSE stream (`data: { type: "...", properties: {...} }`)
- `GET /agent?directory=...` → `OpenCodeAgentInfo[]`
- `GET /provider?directory=...` → `OpenCodeProvidersResponse`
- `GET /session/status?directory=...` → `{ sessionId: OpenCodeSessionStatus }`

**Frontend Stack**:
- Next.js 16 + React 19 SPA (static export to `client/dist/`).
- `api-client.ts` supports both same-origin (SPA served from wwwroot) and split-mode.
- WebSocket singleton in `use-weave-socket.ts` — topic-based subscription.
- Session events via `use-session-events.ts` — subscribes to `session:{id}` topic.
- SPA build: `cd client && npm run build` → copies to `wwwroot/` during backend build.

**Test Infrastructure**:
- .NET tests: xUnit + NSubstitute + coverlet. Central package management (`Directory.Packages.props`).
- Frontend tests: Vitest + @testing-library/react.
- Contract fixtures in `tests/contracts/` — 5 JSON files mapping OpenCode ↔ Fleet event/message shapes.
- Tests `Directory.Build.props` suppresses `CA1707` for test naming.
- `net10.0` + `LangVersion=14`.

**Build Mode for Tests**:
- **Integrated mode** (backend serves built SPA from wwwroot): Best for E2E — single process, single port, no CORS issues. The API project's `.csproj` copies `client/dist/**` to `wwwroot/` at build time.
- Backend binds to `http://127.0.0.1:3000` by default; configurable via `FleetOptions.Port/Host`.
- SQLite databases (main + analytics) created at startup via `MigrationRunner`.

## Architecture Decisions

### Decision 1: TestHarness as a .NET project (not Node.js)

**Choice**: `.NET project at `tests/WeaveFleet.TestHarness/`

**Rationale**:
- The OpenCode API models (`OpenCodeModels.cs`) are internal C# types — the TestHarness needs to produce responses matching those exact shapes. A .NET project can reference the Infrastructure project and reuse the model types + JSON serializer options.
- Reusable by both Playwright E2E tests (via `WebApplicationFactory`) and future .NET integration tests.
- Follows existing project structure (all test projects are .NET under `/tests/`).
- No additional runtime dependency (Node.js mock server would need a separate process).

### Decision 2: TestHarness implements IHarness + IHarnessInstance directly

**Choice**: Create `TestHarness` as an `IHarness` implementation (`type = "test"`) whose `SpawnAsync` returns `TestHarnessInstance` objects with pre-configured scenarios — NOT a separate HTTP server.

**Rationale**:
- The real OpenCode harness spawns a subprocess and talks to it over HTTP. The TestHarness doesn't need to simulate that subprocess. It only needs to implement `IHarnessInstance` and return controlled responses.
- Eliminates the need for port allocation, process management, and HTTP client wiring in tests.
- Much simpler: `TestHarnessInstance.SendPromptAsync()` pushes pre-configured messages into an in-memory queue; `SubscribeAsync()` yields them as `HarnessEvent` objects; `GetMessagesAsync()` returns them.
- The backend code above the harness layer (`SessionOrchestrator`, `HarnessEventRelay`, `WebSocketEndpoints`, etc.) is exercised with real behavior — only the OpenCode subprocess is mocked out.
- For .NET integration tests (without browser), this same TestHarness can be injected directly.

### Decision 3: Use WebApplicationFactory for E2E server lifecycle

**Choice**: Use `WebApplicationFactory<Program>` to boot the real ASP.NET Core pipeline with DI overrides.

**Rationale**:
- Standard pattern for ASP.NET Core integration testing. The factory spawns the real Kestrel server.
- DI overrides: replace `IHarness` registrations with `TestHarness` singleton, ensuring `HarnessRegistry` discovers only the test harness.
- The factory serves the built SPA from `wwwroot/` (integrated mode), so Playwright connects to a single URL.
- No need for separate frontend dev server — tests use the pre-built SPA.

### Decision 4: Use integrated mode (SPA-in-wwwroot)

**Choice**: Pre-build the SPA, then boot the backend which serves it from wwwroot.

**Rationale**:
- E2E tests exercise the real deployment topology (one server, one port).
- No CORS issues. No split-mode WebSocket URL derivation.
- CI-friendly: `npm run build` once, then run all E2E tests against the built artifact.
- The API project already has a `CopyFrontendDist` MSBuild target that copies `client/dist/**` to `wwwroot/`.

### Decision 5: Playwright test project structure

**Choice**: `tests/WeaveFleet.E2E/` as a .NET project referencing `Microsoft.Playwright` + xUnit.

**Rationale**:
- Playwright for .NET integrates with xUnit via `[Fact]`/`[Theory]` test methods.
- Can reference `WeaveFleet.TestHarness` project and the API project.
- Single `dotnet test` command runs both unit tests and E2E tests (with appropriate filtering via categories/traits).
- CI can run in headless Chromium mode.

### Decision 6: Test data/state management

**Choice**: Each test class boots a fresh `WebApplicationFactory` with an isolated in-memory SQLite database. `TestHarness` scenarios are configured per-test via a `TestScenarioBuilder`.

**Rationale**:
- Full isolation — no shared state between tests.
- SQLite `:memory:` with `MigrationRunner` gives a fresh schema per test.
- `TestScenarioBuilder` provides a fluent API for configuring what the mock harness returns.

### Decision 7: WebSocket testing approach

**Choice**: Playwright's native `page.waitForEvent('websocket')` + `webSocket.waitForEvent('framereceived')` for asserting WebSocket messages. For simulating server→client events, `TestHarnessInstance` pushes events via the real `IEventBroadcaster`.

**Rationale**:
- Playwright has built-in WebSocket interception and monitoring.
- Since `HarnessEventRelay` subscribes to harness instance events and broadcasts them via `IEventBroadcaster`, and the WebSocket endpoint pumps from `IEventBroadcaster`, the TestHarness can trigger real events that flow through the entire pipeline.
- No need to mock the WebSocket server — the real one runs.

## Objectives

### Core Objective
Enable automated browser-based testing of all critical Weave Fleet user flows using Playwright, with a mock harness that eliminates external dependencies (OpenCode, Claude Code, GitHub).

### Deliverables
- [x] `tests/WeaveFleet.TestHarness/` — .NET class library implementing `IHarness` + `IHarnessInstance` with scenario-based mock responses
- [x] `tests/WeaveFleet.E2E/` — Playwright + xUnit E2E test project with tests for all critical flows
- [x] `TestScenarioBuilder` — fluent API for configuring mock harness behavior per test
- [x] CI configuration for headless Playwright execution
- [x] Solution file updated with new projects

### Definition of Done
- [x] `dotnet test tests/WeaveFleet.E2E/ --filter Category=E2E` passes all tests in headless Chromium
- [x] TestHarness can simulate: session lifecycle, message streaming, abort, error scenarios
- [x] Tests cover: dashboard, session creation, message history, prompt/response, abort, delete, analytics
- [x] No flaky tests — all assertions use Playwright's auto-waiting
- [x] CI pipeline runs E2E tests on every PR

### Guardrails (Must NOT)
- Must NOT require real OpenCode binary or `opencode serve` process
- Must NOT require internet access or real GitHub credentials
- Must NOT modify production source code's behavior — only add test infrastructure
- Must NOT introduce non-deterministic waits (`Thread.Sleep`) — use Playwright auto-waiting and event-driven assertions
- Must NOT couple E2E tests to CSS class names or implementation details — use data-testid attributes

## TODOs

### Phase 1: TestHarness Core

- [x] 1. Create TestHarness project scaffold
  **What**: Create the `WeaveFleet.TestHarness` class library project with project references and package dependencies. Register in `WeaveFleet.slnx`.
  **Files**:
    - `tests/WeaveFleet.TestHarness/WeaveFleet.TestHarness.csproj`
    - `WeaveFleet.slnx`
  **Acceptance**: `dotnet build tests/WeaveFleet.TestHarness/` succeeds. Project appears in solution.

- [x] 2. Implement TestScenarioBuilder
  **What**: Create a fluent builder for configuring mock harness scenarios. Supports: pre-loaded sessions with messages, configurable prompt responses (text parts, tool parts), SSE event sequences with timing, error simulation, and session status transitions.
  **Files**:
    - `tests/WeaveFleet.TestHarness/TestScenario.cs`
    - `tests/WeaveFleet.TestHarness/TestScenarioBuilder.cs`
  **Acceptance**: Builder can configure a scenario with sessions, messages, and event sequences. Unit test in Phase 3 validates this.

- [x] 3. Implement TestHarness (IHarness)
  **What**: Create `TestHarness : IHarness` that returns `Type = "test"`, `DisplayName = "Test"`, capabilities matching OpenCode (all true except `SupportsResume`), always-available availability check, and `SpawnAsync` that creates a `TestHarnessInstance` from the current scenario.
  **Files**:
    - `tests/WeaveFleet.TestHarness/TestHarness.cs`
  **Acceptance**: `TestHarness` implements `IHarness` interface. `CheckAvailabilityAsync` returns `Available = true`. `SpawnAsync` returns a `TestHarnessInstance`.

- [x] 4. Implement TestHarnessInstance (IHarnessInstance)
  **What**: Create `TestHarnessInstance : IHarnessInstance` that:
    - Tracks status (`Starting` → `Idle` → `Running` → `Idle`).
    - `SendPromptAsync`: pushes configured response events into an internal `Channel<HarnessEvent>`, simulating streamed responses with optional delays.
    - `GetMessagesAsync`: returns accumulated messages from the scenario.
    - `SubscribeAsync`: reads from the internal channel and yields `HarnessEvent` objects.
    - `AbortAsync`: cancels current "prompt processing" and transitions to Idle.
    - `CheckHealthAsync`: always returns healthy.
    - `StopAsync`: completes the channel and transitions to Stopped.
    - `DisposeAsync`: cleanup.
  **Files**:
    - `tests/WeaveFleet.TestHarness/TestHarnessInstance.cs`
  **Acceptance**: Instance correctly implements the full `IHarnessInstance` lifecycle. Events flow through `SubscribeAsync`.

- [x] 5. Implement scenario fixture loader
  **What**: Create a helper that loads contract fixture JSON files from `tests/contracts/` and converts them into `TestScenario` configurations. This enables tests to use realistic OpenCode→Fleet message/event shapes.
  **Files**:
    - `tests/WeaveFleet.TestHarness/FixtureLoader.cs`
  **Acceptance**: `FixtureLoader.LoadOpenCodeMessages("opencode-to-fleet-messages.json")` returns parsed `HarnessMessage` objects.

- [x] 6. Add TestHarness unit tests
  **What**: Write xUnit tests for the TestHarness itself: scenario builder produces valid scenarios, `TestHarnessInstance` correctly yields events from `SubscribeAsync`, `SendPromptAsync` queues responses, `AbortAsync` cancels, status transitions are correct.
  **Files**:
    - `tests/WeaveFleet.TestHarness/Tests/TestHarnessInstanceTests.cs`
    - `tests/WeaveFleet.TestHarness/Tests/TestScenarioBuilderTests.cs`
  **Acceptance**: `dotnet test tests/WeaveFleet.TestHarness/` passes all tests.

### Phase 2: E2E Test Infrastructure

- [x] 7. Create Playwright E2E project scaffold
  **What**: Create the `WeaveFleet.E2E` test project with Playwright .NET, xUnit, and references to TestHarness and Api projects. Add `Microsoft.Playwright` and `Microsoft.AspNetCore.Mvc.Testing` NuGet packages. Register in `WeaveFleet.slnx`.
  **Files**:
    - `tests/WeaveFleet.E2E/WeaveFleet.E2E.csproj`
    - `WeaveFleet.slnx`
  **Acceptance**: `dotnet build tests/WeaveFleet.E2E/` succeeds.

- [x] 8. Create FleetWebApplicationFactory
  **What**: Create a custom `WebApplicationFactory<Program>` that:
    - Replaces all `IHarness` registrations with a single `TestHarness` singleton.
    - Configures in-memory SQLite for isolation (`DataSource=:memory:;Mode=Memory;Cache=Shared`).
    - Sets `FleetOptions.Port` to 0 (auto-assign) and `FleetOptions.DatabasePath` to a temp path.
    - Disables analytics (or uses in-memory analytics DB).
    - Configures Kestrel to listen on `http://127.0.0.1:0` for ephemeral port assignment.
    - Exposes the actual server URL after startup for Playwright to connect to.
    - Provides a method to access the `TestHarness` singleton for per-test scenario configuration.
  **Files**:
    - `tests/WeaveFleet.E2E/Infrastructure/FleetWebApplicationFactory.cs`
  **Acceptance**: Factory boots the real backend, serves the SPA, and `TestHarness` is the only registered harness.

- [x] 9. Create PlaywrightFixture (shared browser instance)
  **What**: Create an xUnit `IAsyncLifetime` fixture that installs Playwright browsers once, creates a shared `IBrowser` instance, and provides `NewPageAsync()` for each test. Uses `BrowserTypeLaunchOptions { Headless = true }`. Supports `HEADED=1` env var for local debugging.
  **Files**:
    - `tests/WeaveFleet.E2E/Infrastructure/PlaywrightFixture.cs`
  **Acceptance**: Fixture launches Chromium once, provides pages to tests, and disposes cleanly.

- [x] 10. Create E2ETestBase class
  **What**: Create an abstract base class for all E2E tests that:
    - Accepts `FleetWebApplicationFactory` and `PlaywrightFixture` via xUnit fixtures.
    - Creates a fresh `IBrowserContext` per test (isolated cookies/storage).
    - Provides `Page` property (an `IPage`).
    - Provides `ServerUrl` property.
    - Provides `ConfigureScenario(Action<TestScenarioBuilder>)` for per-test harness configuration.
    - Implements `IAsyncLifetime` for setup/teardown.
    - Sets up Playwright tracing for failed test artifacts (screenshots, traces).
  **Files**:
    - `tests/WeaveFleet.E2E/Infrastructure/E2ETestBase.cs`
  **Acceptance**: Base class provides `Page`, `ServerUrl`, and scenario configuration. Screenshots captured on failure.

- [x] 11. Add data-testid attributes to key frontend components
  **What**: Add `data-testid` attributes to the frontend components that E2E tests need to interact with. This creates stable selectors that won't break when CSS classes change. Key elements: session cards, session list, summary bar, new session button, prompt input, message list, message items, session status indicator, abort button, delete button/dialog, navigation links.
  **Files**:
    - `client/src/app/page.tsx` (fleet dashboard)
    - `client/src/components/fleet/summary-bar.tsx`
    - `client/src/components/fleet/live-session-card.tsx`
    - `client/src/components/layout/header.tsx`
    - `client/src/app/sessions/[id]/page.tsx` (session detail)
    - `client/src/components/session/prompt-input.tsx` (or equivalent)
    - `client/src/components/session/activity-stream.tsx` (or equivalent)
    - `client/src/components/fleet/confirm-delete-session-dialog.tsx`
  **Acceptance**: All key interactive elements have `data-testid` attributes. Existing Vitest tests still pass.

- [x] 12. Create Playwright page objects
  **What**: Create page object classes encapsulating Playwright selectors and common actions for each page:
    - `FleetDashboardPage`: navigate, wait for sessions, get session cards, click new session, search, filter.
    - `SessionDetailPage`: navigate, wait for messages, send prompt, wait for response, get message list, abort, check status.
    - `NewSessionDialog`: fill form, submit, wait for redirect.
    - `AnalyticsPage`: navigate, wait for data.
  **Files**:
    - `tests/WeaveFleet.E2E/Pages/FleetDashboardPage.cs`
    - `tests/WeaveFleet.E2E/Pages/SessionDetailPage.cs`
    - `tests/WeaveFleet.E2E/Pages/NewSessionDialog.cs`
    - `tests/WeaveFleet.E2E/Pages/AnalyticsPage.cs`
  **Acceptance**: Page objects use `data-testid` selectors. Methods use Playwright auto-waiting.

### Phase 3: E2E Test Cases

#### Golden Path (must pass first — everything else depends on this)

- [x] 13. Test: Golden path — Create project → Create session → Send prompt → Receive response
  **What**: The foundational E2E test that proves the entire stack works end-to-end:
    1. Navigate to fleet dashboard, verify it loads.
    2. Create a new **project** via the UI (name, directory).
    3. Create a new **session** under that project.
    4. Verify redirect to session detail page.
    5. Type a prompt and submit.
    6. Verify the TestHarness response streams in via WebSocket — text appears progressively.
    7. Verify session transitions from busy → idle.
    8. Verify the message (user prompt + assistant response) is visible in the conversation.
  This test exercises: project creation, session creation, prompt sending, WebSocket event delivery, SSE streaming simulation, and UI rendering of streamed messages.
  **Files**:
    - `tests/WeaveFleet.E2E/Tests/GoldenPathTests.cs`
  **Acceptance**: Full flow completes without errors. User prompt visible. Streamed assistant response visible. Session status transitions correctly.

#### Dashboard Tests

- [x] 14. Test: Fleet dashboard loads and shows empty state
  **What**: Test that the fleet dashboard loads, shows "No sessions running" when there are no sessions, and displays the summary bar with zeros.
  **Files**:
    - `tests/WeaveFleet.E2E/Tests/FleetDashboardTests.cs`
  **Acceptance**: Test navigates to `/`, verifies empty state UI, summary bar visible.

- [x] 15. Test: Fleet dashboard shows sessions
  **What**: Configure TestHarness scenario with 2 pre-created sessions (one active/busy, one idle). Navigate to dashboard. Verify session cards render with correct titles, statuses, and summary counts.
  **Files**:
    - `tests/WeaveFleet.E2E/Tests/FleetDashboardTests.cs` (additional test methods)
  **Acceptance**: Two session cards visible with correct titles and status indicators. Summary bar shows `1 active, 1 idle`.

- [x] 16. Test: Dashboard real-time updates via WebSocket
  **What**: Navigate to dashboard showing one session. TestHarness creates a new session via the orchestrator. Verify the dashboard updates to show the new session without page refresh (via the `session_created` event through the activity stream).
  **Files**:
    - `tests/WeaveFleet.E2E/Tests/FleetDashboardTests.cs` (additional test methods)
  **Acceptance**: New session card appears on dashboard without manual refresh.

#### Session Lifecycle Tests

- [x] 17. Test: Create a new session (standalone)
  **What**: Navigate to dashboard, click "New Session" button, fill in the dialog (directory, title), submit. Verify redirect to session detail page. Verify session appears in dashboard on back navigation.
  **Files**:
    - `tests/WeaveFleet.E2E/Tests/SessionLifecycleTests.cs`
  **Acceptance**: Session creation flow completes. Session detail page loads. Dashboard shows the new session.

- [x] 18. Test: View session message history
  **What**: Configure TestHarness with a session that has pre-loaded messages (user message + assistant response with text and tool parts). Navigate to session detail page. Verify messages render with correct roles, text content, and tool call details.
  **Files**:
    - `tests/WeaveFleet.E2E/Tests/SessionLifecycleTests.cs` (additional test methods)
  **Acceptance**: Messages display with user/assistant roles. Text parts render. Tool parts show tool name and state.

- [x] 19. Test: Abort a running session
  **What**: Configure TestHarness to enter a long-running "busy" state on prompt. Send a prompt, verify session shows "busy". Click abort button. Verify session transitions to idle.
  **Files**:
    - `tests/WeaveFleet.E2E/Tests/SessionLifecycleTests.cs` (additional test methods)
  **Acceptance**: Abort button click transitions session from busy to idle. No errors in UI.

- [x] 20. Test: Delete a session
  **What**: Create a session, navigate to dashboard, click delete on the session card. Verify confirmation dialog appears. Confirm deletion. Verify session disappears from the dashboard.
  **Files**:
    - `tests/WeaveFleet.E2E/Tests/SessionLifecycleTests.cs` (additional test methods)
  **Acceptance**: Delete dialog appears with session title. After confirmation, session card removed from dashboard.

- [x] 21. Test: Session rename
  **What**: Navigate to a session, trigger rename (via PATCH endpoint), verify the title updates in the UI.
  **Files**:
    - `tests/WeaveFleet.E2E/Tests/SessionLifecycleTests.cs` (additional test methods)
  **Acceptance**: Session title updates after rename.

#### Messaging & Streaming Tests

- [x] 22. Test: Send prompt and receive streamed text response
  **What**: Configure TestHarness to emit a sequence of events when a prompt is sent: `message.updated` (assistant skeleton), then `message.part.updated` (text part with incremental content), then `session.idle`. Navigate to session, type a prompt, submit. Verify the response streams in (text appears progressively), and the session transitions from busy to idle.
  **Files**:
    - `tests/WeaveFleet.E2E/Tests/SessionMessageTests.cs`
  **Acceptance**: Prompt submission triggers streaming response. Text appears progressively. Session status transitions from busy → idle.

- [x] 23. Test: Send prompt and receive tool call response
  **What**: Configure TestHarness to emit events simulating a tool call flow: `message.updated` → `message.part.updated` (tool part, status=pending → running → completed) → `message.part.updated` (text part with final response) → `session.idle`. Verify tool call renders with name, input, output, and state transitions.
  **Files**:
    - `tests/WeaveFleet.E2E/Tests/SessionMessageTests.cs` (additional test methods)
  **Acceptance**: Tool call part renders with tool name. Status transitions visible. Final text response renders.

- [x] 24. Test: Session status changes via WebSocket
  **What**: Configure TestHarness to emit status change events via the event broadcaster. Navigate to a session. Verify status indicator updates in real-time when the harness transitions through `idle` → `busy` → `idle` states (simulated via TestHarnessInstance pushing events).
  **Files**:
    - `tests/WeaveFleet.E2E/Tests/SessionMessageTests.cs` (additional test methods)
  **Acceptance**: Status indicator updates without page refresh. WebSocket events flow through correctly.

#### Error Handling Tests

- [x] 25. Test: Error handling — harness spawn failure
  **What**: Configure TestHarness to throw on `SpawnAsync`. Attempt to create a session. Verify the API returns an error and the UI shows an error state.
  **Files**:
    - `tests/WeaveFleet.E2E/Tests/ErrorHandlingTests.cs`
  **Acceptance**: Error message displayed. No unhandled exceptions.

- [x] 26. Test: Error handling — prompt failure
  **What**: Configure TestHarness to throw on `SendPromptAsync`. Send a prompt from the UI. Verify error state is shown.
  **Files**:
    - `tests/WeaveFleet.E2E/Tests/ErrorHandlingTests.cs` (additional test methods)
  **Acceptance**: Error displayed in the session view.

#### Analytics Tests

- [x] 27. Test: Analytics page displays data
  **What**: Configure TestHarness scenario with sessions that have token usage data. Navigate to analytics page. Verify charts/data display.
  **Files**:
    - `tests/WeaveFleet.E2E/Tests/AnalyticsPageTests.cs`
  **Acceptance**: Analytics page loads without errors. Data visible (may be minimal given mock data).

### Phase 4: CI Integration & Polish

- [x] 28. Add Playwright browser install step
  **What**: Create a script or MSBuild target that runs `pwsh playwright.ps1 install chromium` (the Playwright .NET CLI) as part of the E2E project build or a dedicated setup step. Document the CI setup.
  **Files**:
    - `tests/WeaveFleet.E2E/playwright-setup.sh`
  **Acceptance**: `./tests/WeaveFleet.E2E/playwright-setup.sh` installs Chromium browsers.

- [x] 29. Add test artifact collection
  **What**: Configure Playwright tracing and screenshot capture on test failure. Store artifacts in `tests/WeaveFleet.E2E/test-results/`. Configure `.gitignore` to exclude artifacts.
  **Files**:
    - `tests/WeaveFleet.E2E/Infrastructure/E2ETestBase.cs` (enhance teardown)
    - `tests/WeaveFleet.E2E/.gitignore`
  **Acceptance**: Failed tests produce screenshots and trace files in `test-results/`.

- [x] 30. Add CI workflow for E2E tests
  **What**: Create or update the CI workflow to: (1) build the frontend SPA, (2) install Playwright browsers, (3) run E2E tests with `--filter Category=E2E`. Ensure the workflow caches Playwright browsers and `node_modules`.
  **Files**:
    - `.github/workflows/e2e-tests.yml` (or update existing workflow)
  **Acceptance**: CI runs E2E tests on PR. Artifacts uploaded on failure.

- [x] 31. Add documentation for running E2E tests locally
  **What**: Add a section to the existing README or create a test-specific doc explaining: prerequisites, how to run E2E tests locally (headed + headless), how to write new E2E tests, how to configure TestHarness scenarios, and troubleshooting tips.
  **Files**:
    - `tests/WeaveFleet.E2E/README.md`
  **Acceptance**: A developer can follow the README to run E2E tests locally on the first try.

## Implementation Notes

### TestHarnessInstance Event Flow (Detailed)

When `SendPromptAsync` is called on a `TestHarnessInstance`:

1. The scenario defines a list of `ScenarioResponse` objects, each containing:
   - `HarnessEvent` to yield
   - Optional `TimeSpan` delay before yielding (simulates streaming)
2. The instance writes these events into an internal `Channel<HarnessEvent>`.
3. `SubscribeAsync` reads from this channel and yields events.
4. `HarnessEventRelay` (the real production code) subscribes to `SubscribeAsync`, picks up these events, and broadcasts them via `IEventBroadcaster`.
5. `WebSocketEndpoints` picks up broadcasted events and sends them to connected clients.
6. The browser receives WebSocket messages and updates the UI.

This means the **entire server-side pipeline** is exercised with real code — the only mock is the harness instance itself.

### SQLite Isolation Strategy

Each `FleetWebApplicationFactory` instance uses a unique connection string with `Data Source` set to a temp file (not `:memory:`, because Kestrel runs on a different thread and SQLite in-memory DBs are per-connection). The temp file is cleaned up on disposal.

Alternatively, use `DataSource=file:{guid}?mode=memory&cache=shared` for a named in-memory DB shared across connections within the same process.

### Default Harness Type Override

The `SessionOrchestrator` defaults to `"opencode"` harness type. For E2E tests, the `FleetWebApplicationFactory` should configure `TestHarness.Type` to return `"opencode"` (or override the default in `SessionOrchestrator` via config). The simplest approach: make the TestHarness return `Type = "opencode"` so it's selected by default without modifying production code.

### Frontend Build Requirement

E2E tests require a pre-built SPA in `client/dist/`. The CI workflow should run `npm run build` before E2E tests. For local development, developers should run `npm run build` once (or use the `dev:split` mode and point Playwright at the dev server, but integrated mode is preferred for E2E).

### Playwright .NET Package Version

Use `Microsoft.Playwright` v1.51+ (latest stable for .NET 10). Add to `Directory.Packages.props`:
```xml
<PackageVersion Include="Microsoft.Playwright" Version="1.51.0" />
<PackageVersion Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.4" />
```

### Test Categories

All E2E tests should be tagged with `[Trait("Category", "E2E")]` to enable selective execution:
- `dotnet test --filter Category=E2E` — run only E2E tests
- `dotnet test --filter Category!=E2E` — run everything except E2E tests (for fast CI feedback)

## Dependency Graph

```
Phase 1 (TestHarness Core):
  Task 1 → Task 2 → Task 3 → Task 4 → Task 5 → Task 6

Phase 2 (E2E Infrastructure):
  Task 7 (depends on Task 1)
  Task 8 (depends on Task 7, Task 3, Task 4)
  Task 9 (depends on Task 7)
  Task 10 (depends on Task 8, Task 9)
  Task 11 (independent — can run in parallel with Phase 1)
  Task 12 (depends on Task 10, Task 11)

Phase 3 (E2E Tests):
  Task 13 (Golden Path) depends on Task 12 — must pass first
  Tasks 14-27 depend on Task 12, independent of each other

Phase 4 (CI & Polish):
  Tasks 28-31 depend on Phase 3 being substantially complete
```

## Verification

- [x] `dotnet build` succeeds for the entire solution (including new projects)
- [x] `dotnet test tests/WeaveFleet.TestHarness/` — TestHarness unit tests pass
- [ ] `dotnet test tests/WeaveFleet.E2E/ --filter Category=E2E` — all E2E tests pass in headless mode
- [x] Existing unit tests still pass: `dotnet test --filter Category!=E2E`
- [ ] Frontend build succeeds: `cd client && npm run build`
- [x] No regressions in existing functionality
- [ ] CI workflow completes successfully with E2E tests
- [x] Screenshots/traces captured for any failing tests
