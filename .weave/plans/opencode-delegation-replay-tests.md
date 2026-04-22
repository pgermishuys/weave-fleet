# OpenCode Delegated-Session Replay Tests

## TL;DR
> **Summary**: Add integration/regression tests **and** Playwright E2E tests for OpenCode delegation lifecycle using a shared replay-fixture strategy derived from the real captured SSE log, covering parent/child model identity, delegation lifecycle, busy-state tracking, the early-child-event race, `session.created` as a first-class event, and browser-level UI assertions for delegated child sessions.
> **Estimated Effort**: Large

## Context
### Original Request
Create realistic OpenCode delegated-session integration/regression tests based on the captured SSE log at `.weave/learnings/opencode-delegated-session.log`.

### Key Findings
1. **Captured log structure**: The log shows a complete delegation flow on a single SSE stream:
   - Parent session `ses_24b6b4488ffe` uses `gpt-5.4` on `github-copilot` agent `Loom (Main Orchestrator)`
   - Parent calls `task` tool (callID `call_M0wkGVVWC43h5DZM8SA16uwE`) to delegate to `thread`
   - `session.created` fires for child `ses_24b6b0ba6ffe` with `parentID` pointing to parent — this event type is **NOT** in `EventTypes.cs` today
   - Child uses `claude-haiku-4.5` on `github-copilot` agent `thread`
   - Parent stays `busy` throughout child execution (multiple `session.status` events with `"type":"busy"` for parent session ID)
   - Child events (messages, tool calls, status) arrive **interleaved** on the same SSE stream with different `sessionID` values

2. **Existing test infrastructure**: `OpenCodeHarnessSessionPersistenceTests` already has:
   - `FakeSseHttpMessageHandler` — accepts raw SSE body string, streams it to `OpenCodeHttpClient`
   - `CreateInstanceWithSseLines()` — builds an `OpenCodeHarnessSession` with fake HTTP + DI
   - `ConsumeEventsAsync()` — drains `SubscribeAsync` and optionally drives persistence
   - Delegation tests for `task` tool pending/running/completed states

3. **`session.created` is NOT a known event type**: `EventTypes.cs` has no `SessionCreated` constant. `OpenCodeMapper.ToHarnessEvent` likely maps it to a generic/unknown type. The log shows this event carries `parentID` and `permission` arrays — critical for child session detection.

4. **Early-child-event race**: In `SubscribeAsync`, child events are routed via `TryResolveChildFleetSessionIdAsync` which does a DB lookup. But `TryEmitDelegationAsync` (which creates the child fleet session via `SessionOrchestrator.EnsureDelegatedChildSessionAsync`) is fire-and-forget. The log shows `session.created` + child `message.updated` arriving in the same SSE batch (same timestamp `11:45:54.024`), meaning child events can arrive before the delegation handler has persisted the child session — causing them to be silently dropped (`continue` on line 259).

5. **Model identity**: Parent messages carry `modelID: "gpt-5.4"`, `providerID: "github-copilot"`. Child messages carry `modelID: "claude-haiku-4.5"`, `providerID: "github-copilot"`. The task tool's `state.metadata.model` also carries child model info.

## Objectives
### Core Objective
Add a test class that replays the real SSE event ordering from the captured log, asserting correct behavior for delegation lifecycle, model routing, parent busy-state, and child event routing — and exposing/fixing the early-child-event drop race.

### Deliverables
- [ ] Shared replay fixture in `WeaveFleet.Testing` usable by both integration and E2E tests
- [ ] `SessionCreated` constant in `EventTypes.cs` (or decision not to)
- [ ] Integration test class with vertical-slice tests covering each concern
- [ ] Fix for the early-child-event drop race in `SubscribeAsync`
- [ ] `data-testid` attributes on delegation link UI elements
- [ ] E2E Playwright test class covering delegation UI, model labels, busy-state, and WebSocket delivery

### Definition of Done
- [ ] `dotnet test --filter "FullyQualifiedName~DelegationReplay"` passes (integration)
- [ ] `dotnet test --filter "FullyQualifiedName~DelegationReplayE2E"` passes (Playwright)
- [ ] `dotnet build -c Release` succeeds with no warnings in touched files

### Guardrails (Must NOT)
- Do NOT modify the captured log file
- Do NOT change existing passing test behavior
- Do NOT add new `data-testid` attributes without verifying they don't collide with existing ones

## TODOs

- [x] 1. Add `SessionCreated` event type constant
  **What**: Add `public const string SessionCreated = "session.created";` to `EventTypes.cs`. Add classification in `EventTypeMetadata.cs` (non-durable, ephemeral, not message-bearing). Update `OpenCodeMapper.ToHarnessEvent` to map `session.created` events, extracting `parentID` from the payload.
  **Files**: `src/WeaveFleet.Domain/Harnesses/EventTypes.cs`, `src/WeaveFleet.Domain/Harnesses/EventTypeMetadata.cs`, `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeMapper.cs`
  **Acceptance**: `EventTypeMetadata.Classify("session.created")` returns a valid classification. Existing tests pass.

- [x] 2. Create shared replay fixture data in `WeaveFleet.Testing`
  **What**: Create `DelegationReplayFixture.cs` in `tests/WeaveFleet.Testing/Fixtures/` so it can be referenced by both `WeaveFleet.Infrastructure.Tests` (unit/integration) and `WeaveFleet.E2E` (Playwright). Extract the key SSE events from the log into static `string` fields — curate to ~25-30 events (not all 200+ delta events). Include: parent `message.updated` (user+assistant), parent `session.status` (busy), parent `message.part.updated` (task tool pending → running with child session metadata), `session.created` for child, child `message.updated`, child `message.part.updated` (tool calls), child `session.status`, parent `session.status` (busy during child work), child final `message.updated` with completion, task tool completed, parent `session.status` (idle). Expose well-known constants: `ParentSessionId`, `ChildSessionId`, `ParentModelId` (`gpt-5.4`), `ChildModelId` (`claude-haiku-4.5`), `ParentProviderId`/`ChildProviderId` (`github-copilot`), `ParentAgent` (`Loom (Main Orchestrator)`), `ChildAgent` (`thread`), `ParentToolCallId`, `ChildTitle` (`Read Program.cs (@thread subagent)`). Also expose a `GetHarnessEvents()` method that returns `IReadOnlyList<HarnessEvent>` — pre-parsed events ready for `TestHarnessSession.PushEventAsync()` — so E2E tests don't need raw SSE parsing.
  **Files**: `tests/WeaveFleet.Testing/Fixtures/DelegationReplayFixture.cs`
  **Acceptance**: Class compiles, `GetSseLines()` returns ordered raw SSE data lines, `GetHarnessEvents()` returns typed events. Both `WeaveFleet.Infrastructure.Tests` and `WeaveFleet.E2E` can reference it.

- [x] 3. Test: parent vs child model/provider identity
  **What**: Replay the fixture, consume events, assert that events with parent session ID carry `modelID=gpt-5.4`, `providerID=github-copilot` and events with child session ID carry `modelID=claude-haiku-4.5`, `providerID=github-copilot`. Use `ExtractModelInfo` pattern from existing tests.
  **Files**: `tests/WeaveFleet.Infrastructure.Tests/Harnesses/OpenCode/OpenCodeDelegationReplayTests.cs`
  **Acceptance**: Test passes asserting distinct model IDs per session.

- [x] 4. Test: delegation lifecycle (pending → running → completed)
  **What**: Replay fixture, verify `InMemoryDelegationRepository` receives: (a) initial delegation with status `pending` and correct `ParentToolCallId`, (b) update linking `ChildSessionId`, (c) completion status. Also verify `FakeEventBroadcaster` receives `delegation.created` and `delegation.updated` broadcasts on the parent session topic.
  **Files**: `tests/WeaveFleet.Infrastructure.Tests/Harnesses/OpenCode/OpenCodeDelegationReplayTests.cs`
  **Acceptance**: Test passes asserting full delegation state machine transitions.

- [x] 5. Test: parent remains busy while child works
  **What**: Replay fixture, collect all `session.status` events. Assert that the parent session ID has `busy` status events both before and during child execution, and that no `idle` status appears for the parent until after the child's work completes.
  **Files**: `tests/WeaveFleet.Infrastructure.Tests/Harnesses/OpenCode/OpenCodeDelegationReplayTests.cs`
  **Acceptance**: Test passes asserting parent busy-state invariant.

- [x] 6. Test: `session.created` event is yielded with parentID
  **What**: Replay fixture, find the `session.created` event in the consumed stream. Assert it carries the child session ID and that the payload contains `parentID` matching the parent session ID. This validates TODO 1.
  **Files**: `tests/WeaveFleet.Infrastructure.Tests/Harnesses/OpenCode/OpenCodeDelegationReplayTests.cs`
  **Acceptance**: Test passes finding exactly one `session.created` event with correct parent/child linkage.

- [x] 7. Fix early-child-event drop race in `SubscribeAsync`
  **What**: The root cause: `TryEmitDelegationAsync` is fire-and-forget (`_ = TryEmitDelegationAsync(sseEvt)`) but child events arriving in the same SSE batch need the child fleet session to already exist in the DB for `TryResolveChildFleetSessionIdAsync` to succeed. Options:
  - **(Recommended)** When `session.created` arrives with a `parentID` matching the parent session, call `TryEmitDelegationAsync`-equivalent logic **synchronously** (awaited) to ensure the child fleet session is created before processing subsequent child events. This leverages the new `session.created` event type from TODO 1.
  - **(Alternative)** Buffer unroutable child events and retry resolution after delegation completes.
  The recommended approach is simpler: detect `session.created`, await delegation/child-session creation, then continue. This means the `session.created` handler in `SubscribeAsync` should `await` rather than fire-and-forget.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHarnessSession.cs`
  **Acceptance**: Replay test from TODO 3/4 passes without dropped child events. Add a specific test that verifies child `message.updated` events arriving immediately after `session.created` are NOT dropped.

- [x] 8. Test: child events immediately after `session.created` are not dropped
  **What**: Explicit regression test for the race condition. Craft an SSE sequence where `session.created` and child `message.updated` arrive back-to-back (same batch). Verify the child `message.updated` event is yielded (not silently dropped). This is the key regression test for TODO 7.
  **Files**: `tests/WeaveFleet.Infrastructure.Tests/Harnesses/OpenCode/OpenCodeDelegationReplayTests.cs`
  **Acceptance**: Test passes. Without the fix from TODO 7, this test would fail (child event dropped).

---

### E2E / Playwright Layer

The E2E tests use the existing `TestHarnessSession.PushEventAsync()` mechanism (same as `SubAgentDelegationTests`) but feed it the shared `DelegationReplayFixture.GetHarnessEvents()` sequence. This validates that the **full vertical** — harness → relay → persistence → WebSocket → Vue UI — works correctly with realistic event ordering.

**Mock layer strategy**: No new mock infrastructure needed. The existing pattern in `SubAgentDelegationTests` is the template:
1. Seed DB rows (workspace, instance, parent session) via Dapper repos
2. Call `DelegationService.HandleDelegationDetectedAsync` + `SessionOrchestrator.EnsureDelegatedChildSessionAsync` to create the delegation and child session
3. Obtain the child's `TestHarnessSession` from `InstanceTracker`
4. Push `DelegationReplayFixture.GetHarnessEvents()` through both parent and child `TestHarnessSession.PushEventAsync()` (parent events to parent harness, child events to child harness — split by `sessionID` in the fixture)
5. Assert UI via Playwright page objects

**Fixture splitting**: `DelegationReplayFixture` should expose `GetParentHarnessEvents()` and `GetChildHarnessEvents()` convenience methods that filter by session ID, so E2E tests can push events to the correct `TestHarnessSession` without manual filtering.

**Stability/maintainability**: The fixture is a hand-curated ~25-event subset (not a raw log dump). Each event field has only the properties needed for test assertions — no encrypted metadata, no redundant delta tokens. If the OpenCode SSE schema evolves, only `DelegationReplayFixture.cs` needs updating. The fixture lives in `WeaveFleet.Testing` (shared) so both integration and E2E tests stay in sync.

- [x] 9. Add `data-testid` attributes for delegation UI elements
  **What**: The delegation link in `ActivityStream.vue` currently uses CSS classes (`delegation-link`, `delegation-link--running`, etc.) but no `data-testid`. Add: `data-testid="delegation-link"` on the `<a>` element, `data-testid="delegation-link-title"` on the title span, `data-testid="delegation-link-status"` on the status span. The `MessageBubble.vue` already has `data-testid="message-model-id"` (line 101) — no change needed there.
  **Files**: `client/src/components/session/ActivityStream.vue`
  **Acceptance**: Playwright can locate delegation links via `Page.GetByTestId("delegation-link")`. Existing E2E tests still pass.

- [x] 10. E2E test: delegation replay shows child session link with correct status progression
  **What**: Create `DelegationReplayE2ETests.cs` in `tests/WeaveFleet.E2E/Tests/`. Seed the parent session + delegation in DB (same pattern as `SubAgentDelegationTests`). Navigate to parent session detail page. Push the parent harness events from the fixture. Assert:
  - Delegation link appears in the activity stream (`data-testid="delegation-link"`)
  - Delegation link shows "Running" status while child is active
  - Click delegation link → navigates to child session detail page with correct `parentSessionId` query param
  - Breadcrumb back to parent is visible and shows parent title
  **Files**: `tests/WeaveFleet.E2E/Tests/DelegationReplayE2ETests.cs`
  **Acceptance**: Test passes asserting delegation link visibility, navigation, and breadcrumb.

- [x] 11. E2E test: child session shows distinct model label
  **What**: After navigating to the child session (from TODO 10), push child harness events from the fixture. Assert that the assistant message bubble shows the child model ID via `data-testid="message-model-id"` containing `claude-haiku-4.5`. Navigate back to parent session and verify parent assistant messages show `gpt-5.4`.
  **Files**: `tests/WeaveFleet.E2E/Tests/DelegationReplayE2ETests.cs`
  **Acceptance**: Test passes asserting `message-model-id` contains the expected model string per session.

- [x] 12. E2E test: parent stays busy while child works, transitions to idle after child completes
  **What**: On the parent session detail page, push events in order: parent busy → child working → child complete → parent idle. Assert:
  - `SessionDetailPage.GetStatusAsync()` returns `"working"` during child execution (use `WaitForBusyAsync`)
  - After pushing the final parent idle event, `WaitForIdleAsync` succeeds
  - The abort button is visible during busy state, hidden after idle
  **Files**: `tests/WeaveFleet.E2E/Tests/DelegationReplayE2ETests.cs`
  **Acceptance**: Test passes asserting status indicator transitions via `data-status` attribute.

- [x] 13. E2E test: live child activity streams via WebSocket without polling
  **What**: Navigate to child session detail. Intercept network requests (`Page.Request += ...`). Push child message events from fixture. Assert:
  - Child message text appears in the activity stream via `WaitForMessageTextAsync`
  - No additional `/api/sessions/{childId}/messages` HTTP requests were made (WebSocket-only delivery, same pattern as existing `SubAgentDelegationTests` line 149-218)
  **Files**: `tests/WeaveFleet.E2E/Tests/DelegationReplayE2ETests.cs`
  **Acceptance**: Test passes asserting WebSocket-only delivery of child events.

## Verification
- [x] All existing tests pass: `dotnet test` in `WeaveFleet.Infrastructure.Tests`
- [x] All existing tests pass: `dotnet test` in `WeaveFleet.Domain.Tests`
- [x] New integration tests pass: `dotnet test --filter "FullyQualifiedName~DelegationReplay"`
- [x] New E2E tests pass: `dotnet test --filter "FullyQualifiedName~DelegationReplayE2E"`
- [x] No regressions in existing E2E delegation test: `dotnet test --filter "FullyQualifiedName~SubAgentDelegation"`
- [x] Release build succeeds: `dotnet build -c Release`
