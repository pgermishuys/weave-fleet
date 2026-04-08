# Task Delegation Card v2 — Revised Design

## TL;DR
> **Summary**: Model delegation as a first-class Fleet entity with harness adapter–side detection inside `OpenCodeHarnessInstance` (not `HarnessEventRelay`), emitting harness-agnostic observations (`DelegationDetected`, `DelegationChildLinked`, `DelegationFinished`) that feed a `DelegationStateService`. The frontend consumes only Fleet-owned delegation DTOs/events and remains entirely harness-agnostic.
> **Estimated Effort**: Medium

## Context

### Original Request
Add a task delegation card to the parent session's activity stream — a card that links to a child session — without leaking harness-specific details through `IHarness.cs` or into the frontend.

### Key Findings (from v1, confirmed in v2 research)

1. **Session hierarchy already exists.** `Session.ParentSessionId`, `SessionCallback`, `GetActiveChildrenAsync` — all in place. What's missing is a *delegation-level* concept bridging a parent tool call to a child session.

2. **Frontend currently uses OpenCode-specific parsing.** `TaskDelegationItem` reads `isTaskToolCall(part)`, `getTaskToolInput(part)`, and `getTaskToolSessionId(part)` — all of which parse `tool="task"` state with hardcoded field names. This violates the principle that the UI must remain harness-agnostic.

3. **Harness adapters detect provider-specific delegation semantics.** `OpenCodeHarnessInstance.SubscribeAsync()` calls `OpenCodeMapper.ToHarnessEvent()` and then fires persistence helpers (`TryPersistMessageAsync`, `TryPersistPartAsync`). Analytics extraction (`TryExtractTokenEvent`) also happens inside the instance's `SubscribeAsync` loop — **not** in `HarnessEventRelay`. Delegation detection follows this same harness adapter pattern: each adapter is responsible for recognizing its provider's delegation semantics and emitting harness-agnostic observations.

4. **`HarnessEventRelay` is a pure broadcast pump.** It reads `HarnessEvent` objects from `IHarnessInstance.SubscribeAsync()` and broadcasts them. It does NOT parse provider-specific payloads — it only rewrites `sessionId` fields. Fleet does **not** detect delegation from raw provider payloads in application/UI layers; detection is exclusively a harness adapter concern.

5. **`Description` field on the delegation entity**: After review, `Description` adds questionable value for MVP. The title (derived from `subagent_type`) is sufficient for the card. The full task input is already visible in the parent tool call's arguments. Removing it simplifies the entity and avoids persisting potentially large prompt text.

### Review Feedback Summary (Keep / Change / Drop)

| Item | Decision | Rationale |
|------|----------|-----------|
| First-class `Delegation` entity + DB table | **Keep** | Core design — decouples UI from harness events |
| `Delegation.Description` field | **Drop** | Not clearly justified for MVP; title is sufficient. Add later if needed. |
| `HarnessEventRelay` calling delegation services | **Change** | Violates layering. Detection belongs in the harness adapter (`OpenCodeHarnessInstance`), not the relay. |
| Double-parsing raw harness JSON | **Change** | Harness adapter detects delegation from its own typed SSE event — no re-parsing needed in any other layer. |
| `SupportsTaskDelegation` capability name | **Change → `SupportsDelegation`** | Cleaner naming. |
| `delegation.created` / `delegation.updated` broadcast events | **Keep** | Fleet-owned delegation DTOs broadcast to the frontend — harness-agnostic by design. |
| Dual-path rollout (new + fallback) | **Keep** | Safe migration; frontend checks delegation events first, falls back to `isTaskToolCall`. |
| Hydration endpoint `GET /api/sessions/{id}/delegations` | **Keep** | Needed for initial page load / reconnect. |
| `pending → running` trigger | **Add** (was unspecified) | Triggered when `ChildSessionId` is attached via session-creation callback flow. |
| Child completion ordering vs deletion | **Add** (was unspecified) | Delegation status updated before session deletion in `SessionOrchestrator.DeleteSessionAsync`. |
| Frontend merge/interleaving behavior | **Add** (was unspecified) | Temporary append + re-anchor when `parentToolCallId` match arrives. |

---

## Ownership Model

### Guiding Principles

1. **Harness adapters own detection.** Each harness adapter (e.g., `OpenCodeHarnessInstance`) is responsible for recognizing its provider's delegation semantics and emitting harness-agnostic observations. Fleet's application and UI layers never inspect raw provider payloads for delegation signals.

2. **`DelegationStateService` owns state and projections.** A Fleet application-layer service (`DelegationStateService`) receives harness-agnostic observations, manages the `Delegation` entity lifecycle, and broadcasts Fleet-owned delegation DTOs/events.

3. **The UI is harness-agnostic.** The frontend consumes only Fleet-owned delegation DTOs (`DelegationDto`) and Fleet-owned broadcast events (`delegation.created`, `delegation.updated`). It never parses tool call state, provider-specific field names, or raw harness payloads.

### Harness-Agnostic Observations

Harness adapters emit the following normalized observations into `DelegationStateService`. These are internal backend signals — not UI contracts:

| Observation | Meaning | Emitted By |
|-------------|---------|------------|
| `DelegationDetected` | A new delegation was recognized (tool call with delegation semantics) | Harness adapter (e.g., `OpenCodeHarnessInstance`) |
| `DelegationChildLinked` | The child session ID for a delegation has been resolved | Harness adapter or `SessionOrchestrator` |
| `DelegationFinished` | The delegation reached a terminal state (completed/error/cancelled) | Harness adapter or `SessionOrchestrator` |

These observations are **not** broadcast to the frontend. `DelegationStateService` processes them, updates the `Delegation` entity, and then broadcasts Fleet-owned `delegation.created` / `delegation.updated` events containing `DelegationDto` payloads.

---

## Architecture Decision: Harness Adapter–Side Detection (Revised)

### Problem with v1 design

v1 proposed that `HarnessEventRelay` calls `OpenCodeMapper.TryExtractDelegation()` and then calls a delegation service. This has two problems:

1. **Layering violation**: `HarnessEventRelay` is infrastructure that bridges harness events to `IEventBroadcaster`. It should not call application-layer services or parse provider-specific payloads. Detection of delegation semantics from raw provider payloads is exclusively a harness adapter responsibility.

2. **Double parsing**: The mapper already parsed the raw OpenCode SSE into a `HarnessEvent` inside `OpenCodeHarnessInstance.SubscribeAsync()`. Having the relay re-parse `evt.Payload` means the same JSON is deserialized twice in two different layers.

### v2 design: Harness adapter detection → DelegationStateService

Delegation detection follows the **same pattern as analytics token extraction** — it happens inside `OpenCodeHarnessInstance.SubscribeAsync()`, after `OpenCodeMapper.ToHarnessEvent()` has produced a normalized `HarnessEvent`. The harness adapter detects provider-specific delegation semantics and emits harness-agnostic observations to `DelegationStateService`. Specifically:

```
OpenCodeHarnessInstance.SubscribeAsync()
  └── for each raw SSE event:
      1. Analytics: TryExtractTokenEvent(sseEvt, ...)    ← existing
      2. Mapping: ToHarnessEvent(sseEvt, ...)             ← existing  
      3. Persistence: TryPersistMessageAsync(evt)         ← existing
      4. Persistence: TryPersistPartAsync(evt)            ← existing
      5. **NEW**: TryEmitDelegationObservation(sseEvt)    ← harness adapter detection
      6. yield return evt                                  ← relay broadcasts this
```

Step 5 calls `OpenCodeMapper.TryExtractDelegation(sseEvt)` which inspects the already-typed `OpenCodeSseEvent` (the raw SSE event before it's mapped to `HarnessEvent`). This is the same input `TryExtractTokenEvent` uses — no re-parsing needed. If delegation semantics are detected, the adapter emits a harness-agnostic observation (`DelegationDetected`, `DelegationChildLinked`, or `DelegationFinished`) to `DelegationStateService` via `IServiceScopeFactory` (same pattern as `TryPersistMessageAsync`).

**Why the raw `OpenCodeSseEvent` and not the normalized `HarnessEvent`?**

The raw SSE event contains the typed `OpenCodeToolPart.State` discriminated union (pending/running/completed/error) and the `tool` name. The normalized `HarnessEvent.Payload` is a `JsonElement` — extracting delegation from it would mean re-parsing JSON that was just serialized. Using the pre-mapping typed SSE event avoids this. This is identical to how `TryExtractTokenEvent` works.

### Where delegation logic lives (revised)

| Component | Responsibility |
|-----------|---------------|
| **`OpenCodeMapper.TryExtractDelegation()`** | Inspects typed `OpenCodeSseEvent`, returns `DelegationExtraction?` with title, toolCallId, status. **Only** place that knows OpenCode's `tool="task"` schema. This is harness adapter detection logic. |
| **`OpenCodeHarnessInstance.SubscribeAsync()`** | Calls `TryExtractDelegation()` on each SSE event. If non-null, emits a harness-agnostic observation to `DelegationStateService` via `IServiceScopeFactory`. Fire-and-forget (same as analytics/persistence). |
| **`DelegationStateService`** (Application) | Receives harness-agnostic observations (`DelegationDetected`, `DelegationChildLinked`, `DelegationFinished`). Manages `Delegation` entity lifecycle (CRUD + status transitions). Broadcasts Fleet-owned `delegation.*` events via `IEventBroadcaster` containing `DelegationDto` payloads. |
| **`SessionOrchestrator.DeleteSessionAsync()`** | Before deleting session: check if it's a child of a delegation → emit `DelegationFinished` observation to `DelegationStateService` → then delete. |
| **`HarnessEventRelay`** | **Unchanged.** Pure broadcast pump. No delegation logic. No provider payload inspection. |

---

## Delegation Entity (Domain)

```
Delegation
├── Id: string (Fleet GUID)
├── ParentSessionId: string (FK → sessions.id)
├── ChildSessionId: string? (FK → sessions.id, null until child is created)
├── ParentToolCallId: string? (correlates to ToolUsePart.ToolCallId in parent stream)
├── Title: string (e.g. "Code Review", "Write Tests" — derived from subagent_type)
├── Status: "pending" | "running" | "completed" | "error" | "cancelled"
├── CreatedAt: DateTimeOffset
├── UpdatedAt: DateTimeOffset
└── CompletedAt: DateTimeOffset?
```

**No `Description` field.** The title (from `subagent_type`) is sufficient for the card. The full task prompt is visible in the parent tool call's arguments if needed.

---

## Delegation Lifecycle (explicit triggers)

```
┌─────────────────────────────────────────────────────────────┐
│ Harness adapter detects delegation semantics in provider    │
│ payload (e.g., OpenCode tool="task" with state "pending")   │
│ and emits DelegationDetected observation                    │
└──────────────────────────┬──────────────────────────────────┘
                           │ DelegationStateService receives observation
                           │ DelegationStateService.HandleDetectedAsync()
                           ▼
              ┌────────────────────────┐
              │    PENDING             │ delegation.created broadcast (DelegationDto)
              │ (ChildSessionId=null)  │ (on topic session:{parentSessionId})
              └──────────┬─────────────┘
                         │
    ┌────────────────────┼───────────────────────────────┐
    │ TRIGGER: pending → running                         │
    │                                                    │
    │ When the child session is created by the provider,  │
    │ the harness adapter detects the child session ID    │
    │ in a subsequent provider event and emits a          │
    │ DelegationChildLinked observation with the Fleet    │
    │ session ID.                                         │
    │                                                    │
    │ DelegationStateService.HandleChildLinkedAsync()     │
    │ sets ChildSessionId and transitions to running.     │
    │                                                    │
    │ Alternatively, DelegationStateService can be called │
    │ from SessionOrchestrator.CreateSessionAsync() when  │
    │ the child session has ParentSessionId set — check   │
    │ if a pending delegation exists for the parent.      │
    └────────────────────┼───────────────────────────────┘
                         ▼
              ┌────────────────────────┐
              │    RUNNING             │ delegation.updated broadcast (DelegationDto)
              │ (ChildSessionId set)   │ { delegationId, childSessionId, status: "running" }
              └──────────┬─────────────┘
                         │
    ┌────────────────────┼───────────────────────────────┐
    │ TRIGGER: running → completed/error                 │
    │                                                    │
    │ When the harness adapter detects a terminal state   │
    │ in the provider payload, it emits a                 │
    │ DelegationFinished observation.                     │
    │                                                    │
    │ ALSO: SessionOrchestrator.DeleteSessionAsync()      │
    │ emits DelegationFinished BEFORE deleting the child  │
    │ session → DelegationStateService updates status.    │
    └────────────────────┼───────────────────────────────┘
                         ▼
              ┌────────────────────────┐
              │    COMPLETED / ERROR   │ delegation.updated broadcast (DelegationDto)
              └────────────────────────┘
```

### Child Completion / Deletion Ordering

**Critical invariant**: Delegation status must be updated *before* the child session is deleted.

In `SessionOrchestrator.DeleteSessionAsync(id)`:
1. Look up session by id
2. Check if this session is the `ChildSessionId` of any delegation → if so, emit `DelegationFinished` observation to `DelegationStateService`
3. Fire `SessionCallbackService.TryFireCallbacksAsync(id)` (existing)
4. Stop harness instance (existing)
5. Delete session record (existing)

This ensures the delegation card transitions to completed/error before the child session row is removed.

---

## Broadcast Events (Fleet-Owned Delegation DTOs)

The following events are **Fleet-owned** and carry `DelegationDto` payloads. The frontend consumes only these — never raw provider payloads or harness-specific structures.

```json
// Topic: "session:{parentSessionId}"
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

## Frontend Behavior (detailed)

The frontend is **entirely harness-agnostic**. It consumes only Fleet-owned delegation DTOs and Fleet-owned broadcast events. It never inspects tool call state, provider-specific field names, or raw harness payloads to determine if something is a delegation.

### DelegationDto (frontend type)

```typescript
interface DelegationDto {
  delegationId: string;
  parentToolCallId: string | null;
  childSessionId: string | null;
  title: string;
  status: "pending" | "running" | "completed" | "error" | "cancelled";
}
```

### Merge / Interleaving in activity stream

The activity stream currently renders `AccumulatedMessage[]` entries. Delegations are a parallel data source that must be **interleaved** into the stream.

**Positioning rules:**

1. **Anchor available**: If `delegation.parentToolCallId` matches a `ToolUsePart.callId` in the message list, the delegation card replaces the default tool-call rendering for that part (same position as the existing `TaskDelegationItem` replacement logic).

2. **Anchor NOT yet available** (race condition — delegation event arrives before the tool-call part is streamed): The delegation card is **temporarily appended** to the end of the activity stream. When the matching tool-call part later arrives via `message.part.updated`, the card is **re-anchored** to that part's position. This is a simple index lookup + move on the next render cycle.

3. **No anchor** (`parentToolCallId` is null): Card is appended to the end of the stream. This handles Fleet-initiated delegations (future work).

**Implementation**: The `ToolCallItem` component checks `delegations.find(d => d.parentToolCallId === part.callId)`. If found, it renders `<DelegationCard>` instead of the default tool card. Unanchored delegations are rendered after the last message.

### Hydration on mount

On initial load, `useSessionEvents` calls `GET /api/sessions/{id}/delegations` in parallel with the existing message load. The response populates `delegations: DelegationDto[]` in state. Fleet-owned broadcast events (`delegation.created` / `delegation.updated`) update this array in real time.

### Cache integration

The `sessionCache` entry type is extended with `delegations: DelegationDto[]`. On unmount, delegations are saved. On remount, they're hydrated alongside messages.

---

## Dual-Path Rollout

1. **Backend ships first.** `DelegationStateService` creates delegation records in the DB and broadcasts Fleet-owned delegation events. The existing `TaskDelegationItem` in the frontend continues to work because the raw `message.part.updated` events still flow unchanged.

2. **Frontend dual-path.** During migration, `ToolCallItem` checks for a matching `DelegationDto` first. If found, renders `<DelegationCard>`. If not found and `isTaskToolCall(part)`, renders the existing `<TaskDelegationItem>`. This allows incremental migration.

3. **Deprecation.** Once the Fleet-owned delegation event path is validated, remove the `isTaskToolCall` fallback and delete `TaskDelegationItem`, `isTaskToolCall`, `getTaskToolInput`, `getTaskToolSessionId`.

---

## Objectives

### Core Objective
A delegation card appears in the parent session's activity stream showing the child session's title, status (pending → running → completed → error), and a link to navigate to the child session — all without the frontend knowing which harness is running. The UI consumes only Fleet-owned delegation DTOs/events.

### Deliverables
- [ ] `Delegation` domain entity and DB table (no `Description` field)
- [ ] `DelegationStateService` in Application layer (receives harness-agnostic observations, manages lifecycle, broadcasts Fleet-owned delegation DTOs)
- [ ] `OpenCodeMapper.TryExtractDelegation()` (harness adapter detection on typed SSE event)
- [ ] `OpenCodeHarnessInstance` wiring (harness adapter emits observations to `DelegationStateService` in `SubscribeAsync`)
- [ ] `SessionOrchestrator.DeleteSessionAsync` ordering (emit `DelegationFinished` observation before deletion)
- [ ] Fleet-owned broadcast events: `delegation.created`, `delegation.updated` (carrying `DelegationDto` payloads)
- [ ] `SupportsDelegation` capability flag
- [ ] `GET /api/sessions/{id}/delegations` hydration endpoint (returns `DelegationDto[]`)
- [ ] Frontend `DelegationCard` component + state accumulator (consumes only `DelegationDto`)
- [ ] Dual-path rendering in activity stream
- [ ] Deprecate `isTaskToolCall` / `getTaskToolInput` / `getTaskToolSessionId`

### Definition of Done
- [ ] Creating a child session via OpenCode's task tool renders a delegation card in the parent
- [ ] Claude Code sessions show delegation UI only when Fleet-initiated delegation is used (post-MVP)
- [ ] No import of harness-specific types in any frontend component — UI consumes only Fleet-owned `DelegationDto`
- [ ] `IHarness.cs` and `IHarnessInstance.cs` are unchanged
- [ ] `HarnessEventRelay.cs` is unchanged (no delegation logic, no provider payload inspection)
- [ ] All existing tests pass; new tests cover delegation lifecycle
- [ ] Release build passes

### Guardrails (Must NOT)
- Must NOT add methods, properties, or delegation-specific types to `IHarness` or `IHarnessInstance`
- Must NOT add delegation logic to `HarnessEventRelay`
- Must NOT detect delegation from raw provider payloads in application or UI layers — detection is exclusively a harness adapter concern
- Must NOT expose harness-specific IDs, metadata, or provider payload structures to the frontend
- Must NOT make the frontend parse tool call state or provider-specific fields to determine if something is a delegation
- Must NOT break existing session creation/callback flows

---

## TODOs

### MVP (Ship First)

- [ ] 1. **Add Delegation domain entity and repository interface**
  **What**: Create `Delegation` entity in `Domain/Entities` and `IDelegationRepository` in `Domain/Repositories`. Entity has: Id, ParentSessionId, ChildSessionId?, ParentToolCallId?, Title, Status, CreatedAt, UpdatedAt, CompletedAt?. Status is a string enum: pending, running, completed, error, cancelled. No `Description` field.
  **Files**: `src/WeaveFleet.Domain/Entities/Delegation.cs`, `src/WeaveFleet.Domain/Repositories/IDelegationRepository.cs`
  **Acceptance**: Entity compiles. Repository interface has: `InsertAsync`, `GetByParentSessionIdAsync`, `GetByChildSessionIdAsync`, `GetByParentToolCallIdAsync(parentSessionId, toolCallId)`, `UpdateStatusAsync`, `UpdateChildSessionIdAsync`, `GetByIdAsync`.

- [ ] 2. **Add database migration for delegations table**
  **What**: Create migration `006_add_delegations_table.sql` as an embedded resource. Schema: `id TEXT PK`, `parent_session_id TEXT NOT NULL FK`, `child_session_id TEXT FK`, `parent_tool_call_id TEXT`, `title TEXT NOT NULL`, `status TEXT NOT NULL DEFAULT 'pending'`, `created_at TEXT NOT NULL`, `updated_at TEXT NOT NULL`, `completed_at TEXT`. Indexes: `ix_delegations_parent_session_id`, `ix_delegations_child_session_id`, `ix_delegations_parent_tool_call_id`.
  **Files**: `src/WeaveFleet.Infrastructure/Migrations/006_add_delegations_table.sql`
  **Acceptance**: Migration applies cleanly on existing databases. FKs reference sessions(id). Follow existing migration patterns (embedded resource, applied by MigrationRunner).

- [ ] 3. **Implement DelegationRepository (Dapper)**
  **What**: Dapper-backed implementation of `IDelegationRepository` following `SessionCallbackRepository` patterns. Register as scoped in DI.
  **Files**: `src/WeaveFleet.Infrastructure/Data/DelegationRepository.cs`, `src/WeaveFleet.Infrastructure/DependencyInjection.cs`
  **Acceptance**: All CRUD methods implemented and registered. Queries use parameterized SQL.

- [ ] 4. **Add DelegationStateService to Application layer**
  **What**: Application service that receives harness-agnostic observations and manages delegation lifecycle. Methods: `HandleDetectedAsync(parentSessionId, parentToolCallId, title)` — creates `Delegation` record + broadcasts Fleet-owned `delegation.created` event with `DelegationDto` payload. `HandleChildLinkedAsync(delegationId, childSessionId)` — sets ChildSessionId, transitions to running, broadcasts `delegation.updated`. `HandleFinishedAsync(delegationId, status)` — transitions to completed/error, sets CompletedAt, broadcasts `delegation.updated`. `GetForSessionAsync(parentSessionId)` — returns `DelegationDto[]`. `FindByParentToolCallAsync(parentSessionId, toolCallId)` — for idempotent create-or-update. Uses `IEventBroadcaster` for real-time updates on topic `session:{parentSessionId}`. The observation method names (`HandleDetectedAsync`, `HandleChildLinkedAsync`, `HandleFinishedAsync`) reflect that this service processes harness-agnostic signals, not provider-specific payloads.
  **Files**: `src/WeaveFleet.Application/Services/DelegationStateService.cs`
  **Acceptance**: Service validates status transitions (pending→running→completed/error/cancelled). Broadcasts Fleet-owned events with `DelegationDto` payloads. Idempotent: calling `HandleDetectedAsync` twice with the same parentToolCallId returns the existing delegation. Unit tests cover lifecycle transitions.

- [ ] 5. **Add SupportsDelegation to HarnessCapabilities**
  **What**: Add `bool SupportsDelegation` property to `HarnessCapabilities`. Set to `true` in `OpenCodeHarness`, `false` in `ClaudeCodeHarness`. Update frontend `HarnessCapabilities` TypeScript type.
  **Files**: `src/WeaveFleet.Domain/Harnesses/HarnessTypes.cs`, `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHarness.cs`, `src/WeaveFleet.Infrastructure/Harnesses/ClaudeCode/ClaudeCodeHarness.cs`, `client/src/lib/api-types.ts`
  **Acceptance**: Capability serialized in `GET /api/harnesses` response. Frontend type includes `supportsDelegation: boolean`.

- [ ] 6. **Add TryExtractDelegation to OpenCodeMapper (harness adapter detection)**
  **What**: Static method `TryExtractDelegation(OpenCodeSseEvent evt, string fleetSessionId)` that inspects the raw typed SSE event for provider-specific delegation semantics. Returns `DelegationExtraction?` record with: `ToolCallId`, `Title` (from `subagent_type`), `Status` (mapped from tool state), `ChildSessionId?` (from `state.metadata.sessionId`). Detection: event type is `message.part.updated`, properties contain a `part` object with `type="tool"` and `tool="task"`. This is the **only** place that knows OpenCode's task tool schema — all provider-specific delegation detection is confined to this harness adapter method. The returned `DelegationExtraction` is a harness-agnostic intermediate used to emit observations to `DelegationStateService`.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeMapper.cs`
  **Acceptance**: Returns non-null for task tool events. Returns null for all other events. Handles all tool states (pending, running, completed, error). Unit tests verify extraction from contract fixtures. New `DelegationExtraction` record defined in the same file or a nearby DTO file.

- [ ] 7. **Wire delegation detection into OpenCodeHarnessInstance.SubscribeAsync()**
  **What**: In the `SubscribeAsync` loop, after the existing analytics/persistence calls, add a fire-and-forget call to `TryEmitDelegationObservation(sseEvt)`. This private method calls `OpenCodeMapper.TryExtractDelegation()` (harness adapter detection). If non-null, uses `IServiceScopeFactory` to resolve `DelegationStateService` and emits the appropriate harness-agnostic observation (`HandleDetectedAsync`, `HandleChildLinkedAsync`, or `HandleFinishedAsync`). Pattern: identical to `TryPersistMessageAsync` / `TryPersistPartAsync`. Must never throw or block the event stream.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHarnessInstance.cs`
  **Acceptance**: Delegation records are created in the DB when the OpenCode harness adapter detects task tool events. Fleet-owned `delegation.created` and `delegation.updated` events appear on the parent session's WebSocket topic. No changes to `HarnessEventRelay.cs`.

- [ ] 8. **Update SessionOrchestrator.DeleteSessionAsync for delegation ordering**
  **What**: In `DeleteSessionAsync`, before stopping the instance and deleting the session: query `IDelegationRepository.GetByChildSessionIdAsync(id)`. If a delegation exists, emit a `DelegationFinished` observation to `DelegationStateService.HandleFinishedAsync(delegationId, "completed")` (or "error" if the session status indicates error). This ensures the delegation card transitions before the child session row is removed.
  **Files**: `src/WeaveFleet.Application/Services/SessionOrchestrator.cs`
  **Acceptance**: When a child session is deleted, its parent's delegation card transitions to completed/error. The delegation status update happens before `sessionRepository.DeleteAsync()`. The Fleet-owned broadcast event fires before deletion.

- [ ] 9. **Add delegation hydration API endpoint**
  **What**: `GET /api/sessions/{id}/delegations` returns the list of `Delegation` records for a parent session, serialized as `DelegationDto[]`. Used by the frontend on initial load. Returns Fleet-owned DTOs only — no harness-specific data.
  **Files**: `src/WeaveFleet.Api/Endpoints/SessionEndpoints.cs`
  **Acceptance**: Returns 200 with `DelegationDto[]`. Returns empty array if no delegations. Returns 404 if session not found.

- [ ] 10. **Add frontend delegation state accumulator**
  **What**: New module `delegation-state.ts` with pure functions: `applyDelegationCreated(prev, event)` and `applyDelegationUpdated(prev, event)` that maintain a `DelegationDto[]`. Follow the pattern of `event-state.ts`. Include re-anchor logic: when a delegation has `parentToolCallId` but no matching tool part yet, it's flagged as unanchored. All types consumed are Fleet-owned `DelegationDto` — no harness-specific types.
  **Files**: `client/src/lib/delegation-state.ts`
  **Acceptance**: Pure functions with unit tests. No React dependencies. Handles idempotent reapplication. No import of harness-specific types.

- [ ] 11. **Wire delegation events into useSessionEvents**
  **What**: Handle `delegation.created` and `delegation.updated` event types (Fleet-owned broadcast events) in the `handleEvent` function. Maintain a `delegations: DelegationDto[]` state alongside `messages`. Load initial delegations from `GET /api/sessions/{id}/delegations` on mount (parallel with message load). Save/restore delegations in `sessionCache`.
  **Files**: `client/src/hooks/use-session-events.ts`
  **Acceptance**: Delegation DTOs are populated on mount and updated in real time via Fleet-owned WebSocket events. Cache includes delegations.

- [ ] 12. **Add isRelevantToSession support for delegation events**
  **What**: Update `isRelevantToSession` in `event-state.ts` to recognize `delegation.created` and `delegation.updated` events (Fleet-owned) and forward them when `properties.parentSessionId === sessionId`.
  **Files**: `client/src/lib/event-state.ts`
  **Acceptance**: Delegation events are correctly routed to the parent session's event handler.

- [ ] 13. **Create DelegationCard React component**
  **What**: Stateless component rendering a delegation: status icon (spinner/check/error), title, and a `<Link>` to the child session. Follows the visual pattern of the existing `TaskDelegationItem` but consumes only Fleet-owned `DelegationDto` props. No tool-state parsing. No provider-specific field access.
  **Files**: `client/src/components/session/delegation-card.tsx`
  **Acceptance**: Renders correctly for all status values. Link navigates to `/sessions/{childSessionId}`. No import of harness-specific types. Consumes only `DelegationDto`.

- [ ] 14. **Integrate DelegationCard into activity stream (dual-path)**
  **What**: In `activity-stream-v1.tsx`, modify `ToolCallItem` to check if a matching `DelegationDto` exists (by `parentToolCallId === part.callId`). If found, render `<DelegationCard>` instead. If not found but `isTaskToolCall(part)`, render the existing `<TaskDelegationItem>` (fallback). Add unanchored delegations after the last message. Pass `delegations` array through props or context.
  **Files**: `client/src/components/session/activity-stream-v1.tsx`
  **Acceptance**: Delegation cards appear at the correct position (anchored to tool call or appended). Both new and legacy paths work during rollout.

- [ ] 15. **Update ActivityStreamV1 props and page wiring**
  **What**: Add `delegations: DelegationDto[]` prop to `ActivityStreamV1Props`. Thread it from the page component that calls `useSessionEvents`. Update `currentSessionId` usage to use Fleet session ID (already the case, but verify).
  **Files**: `client/src/components/session/activity-stream-v1.tsx`, parent page component that renders ActivityStreamV1
  **Acceptance**: Delegations flow from `useSessionEvents` → page → `ActivityStreamV1` → `ToolCallItem` / unanchored rendering. All delegation data is Fleet-owned `DelegationDto`.

### Post-MVP (Later Work)

- [ ] 16. **Deprecate OpenCode-specific task tool helpers**
  **What**: After validating the Fleet-owned delegation event path in production, remove `isTaskToolCall`, `getTaskToolInput`, `getTaskToolSessionId` from `api-types.ts`. Remove `TaskDelegationItem` component. Remove the fallback path in `ToolCallItem`. This completes the transition to a fully harness-agnostic frontend.
  **Files**: `client/src/lib/api-types.ts`, `client/src/components/session/activity-stream-v1.tsx`
  **Acceptance**: No remaining references to these functions. No provider-specific task tool parsing in frontend code. UI consumes only Fleet-owned delegation DTOs.

- [ ] 17. **Fleet-initiated delegation for non-forking harnesses**
  **What**: Add "Delegate Task" button in prompt bar (gated on `SupportsDelegation` or a Fleet-level setting). When clicked, Fleet creates child session + delegation record + callback in one transaction. Claude Code and other non-forking harnesses can delegate via this path. This path bypasses harness adapter detection entirely — `DelegationStateService` is called directly by the Fleet application layer.
  **Files**: TBD — involves new API endpoint, frontend button, delegation orchestration in application layer.
  **Acceptance**: Non-forking harnesses can create delegation cards via Fleet-initiated flow.

---

## Top Risks

| Risk | Mitigation |
|------|-----------|
| **Race: tool event arrives before child session exists in Fleet DB** | Delegation starts as `pending` with `ChildSessionId=null`. A subsequent harness adapter observation or session-creation hook emits `DelegationChildLinked` to transition to `running`. |
| **Race: delegation.created event arrives at frontend before matching tool-call part** | Frontend temporarily appends card at end of stream. When tool-call part arrives, card is re-anchored to correct position. |
| **OpenCode changes its task tool schema** | Only `OpenCodeMapper.TryExtractDelegation()` (harness adapter detection) parses it. Schema changes isolated to one method in one adapter. Contract test fixtures protect against regression. |
| **Multiple SSE events for the same delegation** | `DelegationStateService.HandleDetectedAsync` deduplicates by `parentToolCallId`. If delegation exists for that tool call ID, update rather than create. |
| **Child session deleted before delegation status updated** | Explicit ordering in `DeleteSessionAsync`: emit `DelegationFinished` observation → fire callbacks → delete session. |
| **Performance: extra DB writes per task tool event** | `TryExtractDelegation` returns null for 99%+ of events (only `tool="task"` triggers it). Same amortized cost as analytics extraction. |

---

## Verification

- [ ] All existing tests pass (`dotnet test`, `bun test`)
- [ ] New unit tests: `DelegationStateService` lifecycle transitions, `OpenCodeMapper.TryExtractDelegation` harness adapter detection, `delegation-state.ts` accumulator
- [ ] Integration test: OpenCode harness adapter detects task tool call → emits `DelegationDetected` → `DelegationStateService` creates record → child session stops → `DelegationFinished` emitted → delegation marked completed → card renders in parent
- [ ] Manual verification: delegation card renders in parent stream, links to child, status updates in real time — all via Fleet-owned DTOs
- [ ] `IHarness.cs` diff is zero lines (unchanged)
- [ ] `IHarnessInstance.cs` diff is zero lines (unchanged)
- [ ] `HarnessEventRelay.cs` diff is zero lines (unchanged)
- [ ] No `@opencode-ai/sdk` imports in `client/src/components/` or `client/src/hooks/`
- [ ] No provider-specific payload parsing in application or UI layers
- [ ] Release build passes (`dotnet build -c Release`)
