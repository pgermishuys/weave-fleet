# Agent Busy/Idle State Tracking Overhaul

## TL;DR
Make agent busy/idle state reliable by treating opencode's SSE `session.status` events as the single source of truth, hardening the delivery pipeline to be lossless, removing persisted activity status, surfacing retry state, and retiring the client-side 2500ms idle fallback timer.

## Context
opencode guarantees `session.status { type: "idle" }` at turn end via Effect.ensuring finalizer. Fleet's current pipeline (HarnessEventRelay → InProcessFanOutService → SignalR) silently drops activity_status events on ForwardAsync failure. The persisted `sessions.activity_status` column is written only on INSERT and never updated — it's stale. The client compensates with a 2500ms idle fallback timer that causes false-idle flicker. The pooled instance path lacks synthetic idle on crash/SSE drop.

## Scope
- In scope: Lossless activity-status delivery, one-shot resync on (re)connect, synthetic idle on pooled instance fault, remove persisted activity_status reads/writes, surface retry status E2E, retire client idle fallback timer.
- Out of scope: Changing opencode's SSE protocol, adding steady-state polling, changing session lifecycle (create/delete), modifying DelegationService behavior.
- Constraints / assumptions: Each phase independently shippable. Server authoritative before client timer removal. bun for client, dotnet for server. opencode task-tool delegations keep parent busy (no Fleet-side propagation needed for those).

## Objectives
- Zero silent drops of activity_status events
- Correct state after any reconnection (SSE to opencode, SignalR to client)
- Correct state after pooled instance crash
- No stale persisted status influencing snapshots
- Client displays retry state (attempt, message, next)
- No sub-3s idle fallback timer in client

## Dependencies and Order
1. Phase 1 (Lossless path) must land first — all subsequent phases assume events aren't dropped.
2. Phase 2 (Resync on connect) depends on Phase 1's hardened path to deliver the seeded state.
3. Phase 3 (Pooled synthetic idle) is independent of Phase 2 but logically follows.
4. Phase 4 (Remove persisted status) depends on Phases 1-3 being live (so fallback isn't needed).
5. Phase 5 (Retry status E2E) can parallel Phase 4 but touches same files — sequence after.
6. Phase 6 (Client simplification) depends on all server phases being live and tested.

## Tasks

### Phase 1: Lossless Activity-Status Path

- [ ] 1. Harden InProcessFanOutService for activity_status events
  - **What**: In `ForwardAsync`, detect activity_status event class. On failure, retry up to 3 times with 50ms backoff before logging and dropping. Alternatively, split activity_status into a dedicated synchronous path that bypasses the fire-and-forget channel (preferred: call tracker.Update + broadcaster.Broadcast directly from the relay for this event type, skipping the lossy fan-out).
  - **Files**: `src/WeaveFleet.Infrastructure/EventBus/InProcessFanOutService.cs`, `src/WeaveFleet.Infrastructure/Services/HarnessEventRelay.cs`
  - **Depends on**: None
  - **Acceptance**:
    - activity_status events are never silently dropped (verified by test)
    - Other event types still use existing async path (no perf regression)

- [ ] 2. Add integration test for lossless delivery
  - **What**: SignalR contract test that broadcasts activity_status(busy) then activity_status(idle) rapidly and asserts both arrive at the client hub connection in order.
  - **Files**: `tests/WeaveFleet.IntegrationTests/Sessions/SignalREventContractTests.cs`
  - **Depends on**: Task 1
  - **Acceptance**:
    - Test passes: both events received in order
    - Test fails if retry/direct-path logic is reverted

### Phase 2: One-Shot Resync on (Re)connect

- [ ] 3. Implement resync on SSE stream (re)establishment
  - **What**: When HarnessEventRelay (non-pooled) or SseEventDemultiplexer (pooled) connects/reconnects to an opencode instance's SSE endpoint, immediately issue `GET /session/status` for each bound opencode session. Feed results into SessionActivityTracker and broadcast corrections via IEventBroadcaster. This replaces relying on the stale in-memory state surviving a reconnect gap.
  - **Files**: `src/WeaveFleet.Infrastructure/Services/HarnessEventRelay.cs`, `src/WeaveFleet.Infrastructure/Services/SseEventDemultiplexer.cs` (or equivalent pooled SSE handler), `src/WeaveFleet.Application/Services/SessionActivityTracker.cs`
  - **Depends on**: Task 1
  - **Acceptance**:
    - After SSE reconnect, tracker state matches opencode's actual status
    - Broadcast sent only if state differs from tracker's current value (no spurious idle→idle)

- [ ] 4. Add integration test for resync
  - **What**: Simulate SSE disconnect+reconnect; assert that a session left busy during the gap is corrected to idle after reconnect (or stays busy if opencode reports busy).
  - **Files**: `tests/WeaveFleet.IntegrationTests/Sessions/` (new test class or extend existing)
  - **Depends on**: Task 3
  - **Acceptance**:
    - Test passes with correct post-reconnect state

### Phase 3: Synthetic Idle on Pooled Instance Fault

- [ ] 5. Broadcast synthetic idle on pooled instance crash/SSE termination
  - **What**: In the pooled SSE handler (SseEventDemultiplexer or PooledOpenCodeInstanceRegistry fault handler), when an instance's SSE stream terminates unexpectedly or a lease faults permanently: look up all fleet session bindings in PoolDemuxBindingTable for that instance, broadcast `activity_status("idle")` for each, and clear their tracker entries. Use value `"idle"` (not a new "disconnected" — keep it simple; the session's connection status is a separate concern).
  - **Files**: `src/WeaveFleet.Infrastructure/Services/SseEventDemultiplexer.cs`, `src/WeaveFleet.Infrastructure/Pool/PooledOpenCodeInstanceRegistry.cs`, `src/WeaveFleet.Infrastructure/Pool/PoolDemuxBindingTable.cs`
  - **Depends on**: Task 1
  - **Acceptance**:
    - On pooled instance crash, all bound sessions transition to idle in tracker and receive broadcast
    - Mirrors non-pooled HarnessEventRelay finally-block behavior (lines 314-321)

- [ ] 6. Add test for pooled crash → synthetic idle
  - **What**: Unit or integration test: register bindings, simulate instance fault, assert idle broadcast for each binding.
  - **Files**: `tests/WeaveFleet.IntegrationTests/` or `tests/WeaveFleet.Infrastructure.Tests/`
  - **Depends on**: Task 5
  - **Acceptance**:
    - Test passes; each bound session receives idle

### Phase 4: Remove Persisted Activity Status

- [ ] 7. Stop reading persisted activity_status
  - **What**: Replace `?? s.ActivityStatus` fallback in SessionEndpoints.cs (:73, :540, :732) with `?? "idle"`. Replace `?? persistedSnapshot.ActivityStatus` in SessionEventsHub.cs:175 with tracker lookup (already available) `?? "idle"`. Replace `GetStatusCountsInternalAsync` in SessionRepository.cs with a method that queries SessionActivityTracker for counts. Update LegacySessionImporter to not rely on the column for runtime status.
  - **Files**: `src/WeaveFleet.Api/Endpoints/SessionEndpoints.cs`, `src/WeaveFleet.Api/Hubs/SessionEventsHub.cs`, `src/WeaveFleet.Infrastructure/Persistence/SessionRepository.cs`, `src/WeaveFleet.Infrastructure/Import/LegacySessionImporter.cs`
  - **Depends on**: Tasks 1-5 (all server hardening live)
  - **Acceptance**:
    - No code path reads `sessions.activity_status` for runtime status decisions
    - Snapshot merge tests updated to reflect new fallback
    - `dotnet build` passes; existing tests pass or are updated

- [ ] 8. Stop writing persisted activity_status
  - **What**: Remove `activity_status` from INSERT in SessionRepository.cs:27-79. Remove the NULL-set on resume (:386). Keep column in DB for now (drop in next migration cycle).
  - **Files**: `src/WeaveFleet.Infrastructure/Persistence/SessionRepository.cs`
  - **Depends on**: Task 7
  - **Acceptance**:
    - No writes to the column
    - Existing sessions with stale data unaffected (reads already replaced)

- [ ] 9. Update affected tests
  - **What**: Fix SnapshotMergeTests.cs:144-169, SessionDiffEndpointTests, EndpointGuardTests to match new behavior (no persisted status in snapshots, fallback is "idle").
  - **Files**: `tests/WeaveFleet.IntegrationTests/Sessions/SnapshotMergeTests.cs`, other affected test files in `tests/`
  - **Depends on**: Tasks 7-8
  - **Acceptance**:
    - `dotnet test` passes for all test projects

### Phase 5: Surface Retry Status End-to-End

- [ ] 10. Propagate retry status through tracker and broadcast
  - **What**: SessionActivityTracker.Update should store retry metadata (attempt, message, next) when status is "retry". Broadcast payload for activity_status should include these fields when present. Define a small DTO or extend existing event shape.
  - **Files**: `src/WeaveFleet.Application/Services/SessionActivityTracker.cs`, `src/WeaveFleet.Infrastructure/EventBus/InProcessFanOutService.cs` (or wherever broadcast payload is shaped)
  - **Depends on**: Task 9
  - **Acceptance**:
    - Retry events carry attempt/message/next in SignalR broadcast
    - SignalR contract test verifies shape

- [ ] 11. Add SignalR contract test for retry event shape
  - **What**: Assert that a retry event arrives with `{ status: "retry", attempt: N, message: "...", next: "ISO timestamp" }`.
  - **Files**: `tests/WeaveFleet.IntegrationTests/Sessions/SignalREventContractTests.cs`
  - **Depends on**: Task 10
  - **Acceptance**:
    - Test passes with correct shape

### Phase 6: Client Simplification

- [ ] 12. Remove 2500ms idle fallback timer
  - **What**: Delete `IDLE_FALLBACK_MS`, `scheduleIdleFallback`, and all call sites in `use-session-events.ts`. Replace with a 60s last-resort fallback that logs a warning (safety net, should never fire). The server is now authoritative.
  - **Files**: `client/src/composables/use-session-events.ts`
  - **Depends on**: Tasks 1-11 (server fully authoritative)
  - **Acceptance**:
    - No 2500ms timer in codebase
    - 60s fallback exists with console.warn
    - `bun run test` passes in client/

- [ ] 13. Render retry state in UI
  - **What**: Update `deriveSessionStatus` (:957-974) to return a "retry" status with metadata. Update the session status display component to show "Retrying (attempt N)…" with next-retry countdown.
  - **Files**: `client/src/composables/use-session-events.ts`, relevant Vue component(s) displaying session status
  - **Depends on**: Task 12
  - **Acceptance**:
    - UI shows retry state with attempt number
    - `bun run test` passes

- [ ] 14. Simplify parent-child busy propagation
  - **What**: In `deriveSessionStatus`, remove client-side parent-inherits-child-busy logic (opencode handles this server-side). Keep SessionActivityTracker's parent-child propagation ONLY for Fleet-level DelegationService delegations (separate opencode sessions where parent doesn't stay busy). Add comment explaining the distinction.
  - **Files**: `client/src/composables/use-session-events.ts`, `src/WeaveFleet.Application/Services/SessionActivityTracker.cs`
  - **Depends on**: Task 13
  - **Acceptance**:
    - Client doesn't independently compute parent busy from child state
    - DelegationService-spawned delegations still propagate busy to parent via tracker
    - Tests pass

## Verification
```bash
# Server tests
dotnet test tests/WeaveFleet.IntegrationTests -c Debug
dotnet test tests/WeaveFleet.Api.Tests -c Debug
dotnet test tests/WeaveFleet.Application.Tests -c Debug
dotnet test tests/WeaveFleet.Infrastructure.Tests -c Debug

# Client tests
cd client && bun run test

# E2E (requires frontend build)
cd client && bun run build && cd ..
dotnet test tests/WeaveFleet.E2E --filter "Category=E2E"

# Verify no 2500ms timer
rg "2500" client/src/ # should return nothing related to idle fallback
rg "IDLE_FALLBACK" client/src/ # should return nothing or only the 60s safety net
rg "ActivityStatus" src/ # should return nothing (column no longer read/written)
```
