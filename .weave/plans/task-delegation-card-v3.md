# Task Delegation Card v3

## TL;DR
> **Summary**: Add a `Delegation` entity that records when a parent session's tool call spawned a child session. The harness adapter detects this and tells Fleet; Fleet stores the record, broadcasts events, and the UI renders a card — all without the frontend knowing which harness is running.
> **Estimated Effort**: Medium

## Context

### Original Request
Show a delegation card in the parent session's activity stream that links to the child session, its title, and live status — without leaking harness specifics into the UI.

### Key Findings

1. **Session lineage already exists.** `Session.ParentSessionId` captures parent/child structure. It stays. Delegation does **not** replace it — Delegation is an additional record that captures *why* the child was created (which tool call triggered it).

2. **Two distinct relationships:**
   - **Session Lineage** (`ParentSessionId`): structural — "this session was spawned from that session." Every child has this. Forks, manual child sessions, and delegations all set it.
   - **Delegation**: semantic/provenance — "this child session was spawned because of *this specific tool call* in the parent." Only tool-call-triggered child sessions have a Delegation. All delegations imply a parent/child relationship, but not all parent/child relationships are delegations.

3. **Frontend currently parses OpenCode-specific tool state.** `isTaskToolCall(part)`, `getTaskToolInput(part)`, `getTaskToolSessionId(part)` all inspect `tool="task"` with hardcoded field names. This violates harness-agnosticism.

4. **Harness adapter detection pattern exists.** `OpenCodeHarnessInstance.SubscribeAsync()` already does fire-and-forget side-channel work: analytics extraction (`TryExtractTokenEvent`), message persistence (`TryPersistMessageAsync`), part persistence (`TryPersistPartAsync`). Delegation detection follows the same pattern.

5. **`HarnessEventRelay` is a pure broadcast pump.** It does not parse provider payloads. It stays unchanged.

### Key Design Decisions

| Decision | Rationale |
|----------|-----------|
| Delegation does not replace `ParentSessionId` | They serve different purposes. Lineage is structural; Delegation is provenance. |
| No `Description` field on Delegation | Title (from `subagent_type`) is enough for the card. The full prompt is in the parent tool call's arguments. |
| Detection in harness adapter, not relay | Follows existing patterns (analytics, persistence). Keeps relay as pure pump. |
| `DelegationService` (not "StateService") | Simpler name. It owns delegation records and broadcasts delegation events. |

---

## Objectives

### Core Objective
A delegation card appears in the parent session's activity stream showing title, live status, and a link to the child session — driven entirely by Fleet-owned `DelegationDto` data, with zero harness-specific code in the frontend.

### Deliverables
- [ ] `Delegation` domain entity + DB table
- [ ] `IDelegationRepository` + Dapper implementation
- [ ] `DelegationService` in Application layer
- [ ] `OpenCodeMapper.TryExtractDelegation()` — harness adapter detection
- [ ] Wiring in `OpenCodeHarnessInstance.SubscribeAsync()`
- [ ] `DeleteSessionAsync` ordering — update delegation before deleting child
- [ ] `SupportsDelegation` capability flag
- [ ] `GET /api/sessions/{id}/delegations` hydration endpoint
- [ ] `delegation.created` / `delegation.updated` broadcast events
- [ ] Frontend `DelegationCard` component
- [ ] Frontend delegation state management + wiring into `useSessionEvents`
- [ ] Dual-path rendering (new path + legacy fallback)

### Definition of Done
- [ ] Creating a child session via OpenCode's task tool renders a delegation card in the parent
- [ ] No import of harness-specific types in any frontend component
- [ ] `IHarness.cs`, `IHarnessInstance.cs`, `HarnessEventRelay.cs` are unchanged
- [ ] All existing tests pass; new tests cover delegation lifecycle
- [ ] Release build passes (`dotnet build -c Release`)

### Guardrails (Must NOT)
- Must NOT modify `IHarness`, `IHarnessInstance`, or `HarnessEventRelay`
- Must NOT detect delegations outside harness adapters (no delegation logic in relay, application, or UI layers)
- Must NOT expose harness-specific data structures to the frontend
- Must NOT remove or modify `Session.ParentSessionId` — Delegation is additive

---

## TODOs

### Layer 1: Domain + Storage

- [ ] 1. **Delegation entity and repository interface**
  **What**: Create the `Delegation` entity and `IDelegationRepository` interface.
  **Files**: `src/WeaveFleet.Domain/Entities/Delegation.cs`, `src/WeaveFleet.Domain/Repositories/IDelegationRepository.cs`
  **Acceptance**: Compiles. Entity has: `Id` (string, GUID), `ParentSessionId` (string), `ChildSessionId` (string?), `ParentToolCallId` (string?), `Title` (string), `Status` (string: pending/running/completed/error/cancelled), `CreatedAt` (string), `UpdatedAt` (string), `CompletedAt` (string?). Repository interface has: `InsertAsync`, `GetByIdAsync`, `GetByParentSessionIdAsync`, `GetByChildSessionIdAsync`, `GetByParentToolCallIdAsync(parentSessionId, toolCallId)`, `UpdateStatusAsync`, `UpdateChildSessionIdAsync`.

- [ ] 2. **Database migration**
  **What**: Add `006_add_delegations_table.sql` as an embedded resource. Follow existing migration patterns (see `005_add_message_agent.sql`).
  **Files**: `src/WeaveFleet.Infrastructure/Migrations/006_add_delegations_table.sql`
  **Acceptance**: Creates `delegations` table with columns matching the entity. Indexes on `parent_session_id`, `child_session_id`, `parent_tool_call_id`. FK to `sessions(id)`. Migration runner picks it up automatically.

- [ ] 3. **Dapper repository implementation**
  **What**: `DapperDelegationRepository` following the pattern of `DapperSessionCallbackRepository`. Register as scoped in `DependencyInjection.cs`.
  **Files**: `src/WeaveFleet.Infrastructure/Data/Repositories/DapperDelegationRepository.cs`, `src/WeaveFleet.Infrastructure/DependencyInjection.cs`
  **Acceptance**: All repository methods implemented with parameterized SQL. Registered in DI.

### Layer 2: Application Service

- [ ] 4. **DelegationService**
  **What**: Application service that owns the delegation records and their lifecycle. This is the single place that creates/updates delegations and broadcasts events to the frontend. It receives simple method calls from harness adapters (not abstract "observations" — just method calls with plain parameters).

  Methods:
  - `HandleDelegationDetectedAsync(parentSessionId, parentToolCallId, title)` — Creates a new `Delegation` record with status `pending`. Idempotent: if one already exists for the same `(parentSessionId, parentToolCallId)`, returns the existing one. Broadcasts `delegation.created` on topic `session:{parentSessionId}`.
  - `HandleChildLinkedAsync(parentSessionId, parentToolCallId, childSessionId)` — Sets `ChildSessionId`, transitions status to `running`. Broadcasts `delegation.updated`.
  - `HandleDelegationFinishedAsync(delegationId, status)` — Transitions to `completed`/`error`/`cancelled`, sets `CompletedAt`. Broadcasts `delegation.updated`.
  - `GetDelegationsAsync(parentSessionId)` — Returns `DelegationDto[]` for the hydration endpoint.

  Uses `IEventBroadcaster` to broadcast on topic `session:{parentSessionId}`.

  **Files**: `src/WeaveFleet.Application/Services/DelegationService.cs`
  **Acceptance**: Status transitions are validated (pending→running→terminal). Idempotent on create. Broadcasts Fleet-owned events with `DelegationDto` payloads. Unit tests cover lifecycle transitions.

### Layer 3: Harness Adapter Detection

- [ ] 5. **Add `SupportsDelegation` capability**
  **What**: Add `bool SupportsDelegation` to `HarnessCapabilities`. Set `true` in `OpenCodeHarness`, `false` in `ClaudeCodeHarness`. Update the frontend `HarnessCapabilities` TypeScript type.
  **Files**: `src/WeaveFleet.Domain/Harnesses/HarnessTypes.cs`, `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHarness.cs`, `src/WeaveFleet.Infrastructure/Harnesses/ClaudeCode/ClaudeCodeHarness.cs`, `client/src/lib/api-types.ts`
  **Acceptance**: Capability appears in `GET /api/harnesses` response. Frontend type includes `supportsDelegation: boolean`.

- [ ] 6. **OpenCodeMapper.TryExtractDelegation()**
  **What**: Static method on `OpenCodeMapper` that inspects a raw `OpenCodeSseEvent` for delegation semantics. This is the **only** place that knows OpenCode's `tool="task"` schema.

  Detection logic: event type is `message.part.updated`, the `part` has `type="tool"` and `tool="task"`. Returns a `DelegationExtraction` record with: `ToolCallId`, `Title` (from `subagent_type` in input), `Status` (mapped from tool state: pending→pending, running→running, completed→completed, error→error), `ChildSessionId?` (from `state.metadata.sessionId` when available).

  Returns `null` for all non-delegation events (99%+ of events).

  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeMapper.cs`
  **Acceptance**: Returns non-null only for `tool="task"` events. Unit tests verify extraction from fixture data. `DelegationExtraction` record defined alongside the method.

- [ ] 7. **Wire detection into OpenCodeHarnessInstance.SubscribeAsync()**
  **What**: After the existing persistence calls in `SubscribeAsync()`, add a fire-and-forget call to a new private method `TryEmitDelegationAsync(sseEvt)`. This method:
  1. Calls `OpenCodeMapper.TryExtractDelegation(sseEvt, _fleetSessionId)`.
  2. If non-null, resolves `DelegationService` via `IServiceScopeFactory`.
  3. Calls the appropriate method on `DelegationService` based on the extraction status:
     - First time seeing this toolCallId → `HandleDelegationDetectedAsync`
     - Has a `ChildSessionId` → `HandleChildLinkedAsync`
     - Terminal status → `HandleDelegationFinishedAsync`
  4. Must never throw or block the event stream (same pattern as `TryPersistMessageAsync`).

  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHarnessInstance.cs`
  **Acceptance**: Delegation records appear in DB when task tool events are processed. `delegation.created`/`delegation.updated` events broadcast on WebSocket. No changes to `HarnessEventRelay.cs`.

### Layer 4: Session Lifecycle Integration

- [ ] 8. **Update DeleteSessionAsync for delegation ordering**
  **What**: In `SessionOrchestrator.DeleteSessionAsync`, before stopping the instance: look up any delegation where this session is the `ChildSessionId`. If found, call `DelegationService.HandleDelegationFinishedAsync(delegationId, "completed")` (or `"error"` based on session status). This ensures the delegation card shows the final state before the child session is removed.

  Inject `IDelegationRepository` into `SessionOrchestrator`.

  **Files**: `src/WeaveFleet.Application/Services/SessionOrchestrator.cs`
  **Acceptance**: Deleting a child session transitions its parent's delegation card to completed/error before the session row is removed.

### Layer 5: API Endpoint

- [ ] 9. **Delegation hydration endpoint**
  **What**: `GET /api/sessions/{id}/delegations` returns `DelegationDto[]` for a parent session. Used by the frontend on initial page load and reconnect.

  `DelegationDto` shape:
  ```
  {
    delegationId: string,
    parentToolCallId: string | null,
    childSessionId: string | null,
    title: string,
    status: "pending" | "running" | "completed" | "error" | "cancelled"
  }
  ```

  **Files**: `src/WeaveFleet.Api/Endpoints/SessionEndpoints.cs`
  **Acceptance**: Returns 200 with `DelegationDto[]`. Returns empty array if no delegations. Returns 404 if session not found.

### Layer 6: Frontend

- [ ] 10. **Delegation state module**
  **What**: New `delegation-state.ts` with pure functions (no React) that manage a `DelegationDto[]` array:
  - `applyDelegationCreated(prev, event)` → adds new delegation
  - `applyDelegationUpdated(prev, event)` → merges updates by `delegationId`

  Follow the pattern of `event-state.ts`.

  ```typescript
  interface DelegationDto {
    delegationId: string;
    parentToolCallId: string | null;
    childSessionId: string | null;
    title: string;
    status: "pending" | "running" | "completed" | "error" | "cancelled";
  }
  ```

  **Files**: `client/src/lib/delegation-state.ts`
  **Acceptance**: Pure functions with unit tests. Idempotent reapplication. No React or harness-specific imports.

- [ ] 11. **Wire delegation events into useSessionEvents**
  **What**: In `useSessionEvents`:
  1. Add `delegations` state (`DelegationDto[]`).
  2. On mount, fetch `GET /api/sessions/{id}/delegations` in parallel with message load.
  3. In `handleEvent`, handle `delegation.created` and `delegation.updated` event types using the functions from step 10.
  4. Include delegations in `sessionCache` save/restore.
  5. Export `delegations` in `UseSessionEventsResult`.

  **Files**: `client/src/hooks/use-session-events.ts`
  **Acceptance**: Delegations populate on mount and update in real time via WebSocket. Cache includes delegations.

- [ ] 12. **Route delegation events in isRelevantToSession**
  **What**: Update `isRelevantToSession` in `event-state.ts` to recognize `delegation.created` and `delegation.updated` events. Match when `properties.parentSessionId === sessionId`.
  **Files**: `client/src/lib/event-state.ts`
  **Acceptance**: Delegation events are forwarded to the correct session's handler.

- [ ] 13. **DelegationCard component**
  **What**: Stateless React component that renders a delegation: status icon (spinner/check/error), title, and a link to the child session. Visually similar to the existing `TaskDelegationItem` but consumes only `DelegationDto` props.
  **Files**: `client/src/components/session/delegation-card.tsx`
  **Acceptance**: Renders all status values correctly. Link navigates to child session. No harness-specific imports.

- [ ] 14. **Integrate DelegationCard into activity stream (dual-path)**
  **What**: In `activity-stream-v1.tsx`, modify `ToolCallItem`:
  1. Check if a matching `DelegationDto` exists (`parentToolCallId === part.callId`).
  2. If found → render `<DelegationCard>`.
  3. Else if `isTaskToolCall(part)` → render existing `<TaskDelegationItem>` (fallback during migration).
  4. Else → render default tool card.

  Thread `delegations: DelegationDto[]` from the page component through to `ToolCallItem`.

  Also render any "unanchored" delegations (those with no matching tool call part yet) at the end of the stream.

  **Files**: `client/src/components/session/activity-stream-v1.tsx`
  **Acceptance**: Delegation cards appear at the correct position. Both new and legacy paths work during rollout.

- [ ] 15. **Thread delegations prop through page component**
  **What**: Add `delegations: DelegationDto[]` prop to `ActivityStreamV1`. Thread it from the page component that calls `useSessionEvents`.
  **Files**: `client/src/components/session/activity-stream-v1.tsx`, parent page component
  **Acceptance**: Delegations flow from `useSessionEvents` → page → `ActivityStreamV1` → `ToolCallItem`.

### Post-MVP

- [ ] 16. **Remove legacy task tool helpers**
  **What**: After validating the new path, remove `isTaskToolCall`, `getTaskToolInput`, `getTaskToolSessionId` from `api-types.ts`. Remove `TaskDelegationItem` from `activity-stream-v1.tsx`. Remove the fallback branch in `ToolCallItem`.
  **Files**: `client/src/lib/api-types.ts`, `client/src/components/session/activity-stream-v1.tsx`
  **Acceptance**: No remaining references to these functions. UI is fully driven by Fleet-owned `DelegationDto`.

---

## Broadcast Events

Fleet broadcasts these on topic `session:{parentSessionId}`:

```json
{
  "type": "delegation.created",
  "properties": {
    "delegationId": "fleet-del-abc",
    "parentSessionId": "fleet-ses-123",
    "childSessionId": null,
    "parentToolCallId": "tool-call-abc",
    "title": "Code Review",
    "status": "pending"
  }
}

{
  "type": "delegation.updated",
  "properties": {
    "delegationId": "fleet-del-abc",
    "childSessionId": "fleet-ses-456",
    "status": "running"
  }
}

{
  "type": "delegation.updated",
  "properties": {
    "delegationId": "fleet-del-abc",
    "status": "completed"
  }
}
```

---

## Delegation Lifecycle

```
Harness adapter detects tool="task" with state "pending"
  → calls DelegationService.HandleDelegationDetectedAsync()
  → Delegation created (status: pending, childSessionId: null)
  → broadcasts delegation.created

Harness adapter detects child session ID in subsequent event
  → calls DelegationService.HandleChildLinkedAsync()
  → Delegation updated (status: running, childSessionId set)
  → broadcasts delegation.updated

Harness adapter detects terminal state (completed/error)
  OR SessionOrchestrator.DeleteSessionAsync() runs for child
  → calls DelegationService.HandleDelegationFinishedAsync()
  → Delegation updated (status: completed/error, completedAt set)
  → broadcasts delegation.updated
```

---

## Dual-Path Rollout

1. **Backend ships first.** Delegation records are created and events broadcast. The existing `TaskDelegationItem` continues working because raw `message.part.updated` events still flow.

2. **Frontend dual-path.** `ToolCallItem` checks for a `DelegationDto` match first. If found, renders `<DelegationCard>`. If not, falls back to `isTaskToolCall(part)` → `<TaskDelegationItem>`.

3. **Deprecation.** Once validated, remove the fallback and delete all `isTaskToolCall` / `getTaskToolInput` / `getTaskToolSessionId` helpers plus `TaskDelegationItem`.

---

## Risks

| Risk | Mitigation |
|------|-----------|
| Tool event arrives before child session exists | Delegation starts as `pending` with `ChildSessionId=null`. Linked when child appears. |
| `delegation.created` arrives before matching tool-call part in UI | Card appended at end of stream, re-anchored when tool part arrives. |
| OpenCode changes task tool schema | Only `OpenCodeMapper.TryExtractDelegation()` knows the schema. One method, one adapter. |
| Duplicate SSE events for same delegation | `HandleDelegationDetectedAsync` is idempotent — deduplicates by `(parentSessionId, parentToolCallId)`. |
| Child session deleted before delegation updated | Explicit ordering in `DeleteSessionAsync`: update delegation status → fire callbacks → delete session. |

---

## Verification

- [ ] All existing tests pass (`dotnet test`, `bun test`)
- [ ] New unit tests: `DelegationService` lifecycle, `OpenCodeMapper.TryExtractDelegation`, `delegation-state.ts` accumulator
- [ ] Integration test: task tool call → delegation created → child linked → child stops → delegation completed → card renders
- [ ] Manual: delegation card renders in parent stream, links to child, status updates live
- [ ] `IHarness.cs` diff is zero lines
- [ ] `IHarnessInstance.cs` diff is zero lines
- [ ] `HarnessEventRelay.cs` diff is zero lines
- [ ] No harness-specific imports in frontend components/hooks
- [ ] Release build passes (`dotnet build -c Release`)
