# Parent Busy-State Derivation from Delegated Children

## TL;DR
> **Summary**: When a delegated child session is busy, the parent session should also appear busy in both the session list and detail views. Currently only `sessionStatus` aggregates children (via DB `status='active'`), but `activityStatus` — the real-time busy/idle signal — is per-session only.
> **Estimated Effort**: Medium

## Context
### Original Request
Parent sessions should show as busy when any delegated child is busy, even if the parent itself is idle (e.g., waiting on a `task` tool call).

### Key Findings
1. **`SessionActivityTracker`** (singleton, in-memory) tracks ephemeral `activityStatus` per `fleetSessionId`. Updated by `EphemeralEventRelayService.ParseActivityStatus()` on `session.status` events.
2. **`EphemeralEventRelayService`** broadcasts `activity_status` on the `"sessions"` topic with `{ sessionId, activityStatus }`. No parent-awareness.
3. **`DeriveAggregatedSessionStatus()`** in `SessionEndpoints.cs` already computes parent "active" via `parentIdsWithBusyChildren` — but that set comes from **DB query** (`GetIdsWithActiveChildrenAsync`) checking `status = 'active'`, NOT ephemeral activity status. This is the wrong signal for real-time busy state.
4. **Frontend detail view** (`sessions.$id.tsx` line 292) computes `effectiveActivityStatus` from the session's own `activityStatus` only.
5. **Frontend list view** (`SessionItem.vue` line 123) checks `props.session.activityStatus === "busy"` — own status only.
6. **Delegation state** exists on frontend: `delegations` ref tracks child `delegationId`/`childSessionId`/`status`. But no child activity status is tracked.
7. **Parent-child link**: `Session.ParentSessionId` in DB. `SessionActivityTracker` has no parent/child awareness.

### Architecture Decision: Backend vs Frontend
**Recommended: Backend-driven.** The `SessionActivityTracker` should derive parent busy state because:
- It already has all session activity states in memory (O(1) lookup).
- The `"sessions"` topic broadcast already fires on every activity change — adding parent propagation here means zero new WebSocket channels.
- Frontend would need to subscribe to N child session topics to do this client-side, which is fragile and doesn't scale.
- The session list API can cheaply consult the tracker instead of (or in addition to) the DB query.

**Alternative considered**: Frontend derives from `delegations` array + subscribing to child session topics. Rejected — requires new subscriptions per delegation, races on connect/disconnect, and duplicates logic.

## Objectives
### Core Objective
A parent session appears busy in both list and detail views whenever any of its delegated children are busy.

### Deliverables
- [x] Backend: `SessionActivityTracker` propagates child busy→parent busy
- [x] Backend: Session list API uses tracker for real-time parent busy state
- [x] Backend: WebSocket initial snapshot includes derived parent state
- [x] Frontend: Session list and detail consume the already-provided `activityStatus` (minimal/no change expected)
- [x] Tests covering multi-child, stale, and edge cases

### Definition of Done
- [x] Parent shows `activityStatus: "busy"` in list when child is busy and parent is idle
- [x] Parent reverts to own status when all children go idle/complete
- [x] E2E delegation replay test verifies parent busy propagation
- [x] No new WebSocket topics or API contracts required

### Guardrails (Must NOT)
- Must NOT add new WebSocket subscriptions or topics
- Must NOT persist derived parent activity status to DB (it's ephemeral)
- Must NOT change the `DelegationDto` contract
- Must NOT break the existing `sessionStatus` aggregation

## TODOs

- [x] 1. **Add parent-child index to SessionActivityTracker**
  **What**: Add a `ConcurrentDictionary<string, string>` mapping `childSessionId → parentSessionId`. Expose `RegisterChild(childId, parentId)` and `UnregisterChild(childId)`. Add `GetEffectiveActivityStatus(sessionId)` that returns "busy" if the session itself is busy OR any registered child is busy.
  **Files**: `src/WeaveFleet.Application/Services/SessionActivityTracker.cs`
  **Acceptance**: Unit test — register child, update child to busy, `GetEffectiveActivityStatus(parentId)` returns "busy".

- [x] 2. **Wire child registration on delegation lifecycle**
  **What**: When `DelegationService.HandleChildLinkedAsync` succeeds (child session ID known), call `_activityTracker.RegisterChild(childSessionId, parentSessionId)`. When delegation finishes (completed/failed/cancelled), call `UnregisterChild`. This is fire-and-forget, same as existing delegation handling.
  **Files**: `src/WeaveFleet.Application/Services/DelegationService.cs` (or wherever `HandleChildLinkedAsync`/`HandleDelegationFinishedAsync` live)
  **Acceptance**: After delegation link, tracker knows the parent-child mapping.

- [x] 3. **Propagate parent activity_status on child status change**
  **What**: In `EphemeralEventRelayService`, after updating the tracker for a child session, check if the child has a registered parent. If so, broadcast an `activity_status` event on the `"sessions"` topic for the **parent** session ID with the effective status. Also broadcast on `session:{parentId}` topic so the detail view gets it.
  **Files**: `src/WeaveFleet.Infrastructure/Nats/EphemeralEventRelayService.cs`
  **Acceptance**: When child goes busy, parent's topic gets an activity_status=busy broadcast. When child goes idle and parent has no other busy children, parent gets idle.

- [x] 4. **Use tracker for session list API busy state**
  **What**: In `SessionEndpoints.ToListResponse`, consult `SessionActivityTracker.GetEffectiveActivityStatus(session.Id)` to set `ActivityStatus` instead of (or overlaid on) `session.ActivityStatus` from DB. This replaces the need for the DB-based `GetIdsWithActiveChildrenAsync` for activity status (keep it for `sessionStatus` if still needed).
  **Files**: `src/WeaveFleet.Api/Endpoints/SessionEndpoints.cs`
  **Acceptance**: GET /api/sessions returns parent with `activityStatus: "busy"` when child is busy.

- [x] 5. **WebSocket initial snapshot includes derived state**
  **What**: In `WebSocketEndpoints`, the initial snapshot already reads from `SessionActivityTracker.Get()`. Change to use `GetEffectiveActivityStatus()` so page refresh shows correct derived state.
  **Files**: `src/WeaveFleet.Api/Endpoints/WebSocketEndpoints.cs`
  **Acceptance**: WebSocket connect for parent session receives busy status when child is busy.

## Edge Cases

| Scenario | Expected Behavior |
|---|---|
| Multiple children busy | Parent busy. One child goes idle → still busy (other child). Last child idle → parent uses own status. |
| Child busy, parent idle | Parent shows busy. |
| Child busy, parent also busy | Parent shows busy (no change). |
| Child completes (delegation finished) | Unregister child. Parent reverts to own status. |
| Stale child (harness disconnects) | `HarnessEventRelay` already calls `_activityTracker.Remove(childSessionId)` on disconnect. `UnregisterChild` should also fire. Parent reverts. |
| App restart | Tracker is empty (ephemeral). All sessions appear idle until next event. This is existing behavior — acceptable. |
| Parent has no harness (stopped) | `DeriveAggregatedSessionStatus` already short-circuits for stopped/completed. No change. |

## Verification
- [x] Unit tests: `SessionActivityTracker` — register/unregister child, effective status derivation, multi-child scenarios
- [x] Unit tests: `EphemeralEventRelayService` — child busy triggers parent broadcast
- [x] Integration test: delegation replay fixture — parent busy state transitions match expected sequence
- [x] E2E: `DelegationReplayE2ETests` — verify parent session shows busy indicator during child execution
- [ ] Manual: open parent session detail, trigger delegation, observe busy spinner
- [x] `dotnet test` passes across all test projects
- [x] No new API contract fields required — verify `SessionListResponse` and WebSocket payloads unchanged
