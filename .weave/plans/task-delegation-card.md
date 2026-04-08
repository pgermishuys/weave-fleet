# Task Delegation Card — Harness-Agnostic Design

## TL;DR
> **Summary**: Model task delegation as a first-class Fleet domain concept (`Delegation`) backed by a DB table and generic broadcast events, so the UI renders delegation cards without knowing which harness created them. Harness mappers normalize provider-specific tool calls into a single `DelegationRequested` event; the UI consumes only Fleet-owned types.
> **Estimated Effort**: Medium

## Context

### Original Request
Add a task delegation card to the Weave Fleet UI — a card in the parent session's activity stream that links to a child session — without leaking harness-specific details through `IHarness.cs` or into the frontend.

### Key Findings

1. **Session hierarchy already exists.** `Session.ParentSessionId` is persisted, `session_callbacks` tracks completion notifications, and `ISessionRepository.GetActiveChildrenAsync` / `GetIdsWithActiveChildrenAsync` are implemented. The plumbing for parent/child relationships is in place; what's missing is a *delegation-level* concept that bridges a specific tool call in the parent to a specific child session.

2. **The frontend already renders task delegation cards.** `TaskDelegationItem` in `activity-stream-v1.tsx` detects `tool === "task"` parts, extracts `subagent_type`, `description`, and the child session ID from OpenCode-specific tool state metadata. This works — but it's entirely OpenCode-shaped: it reads `state.metadata.sessionId`, parses `task_id: ses_xxx` from output strings, and assumes OpenCode's URL patterns.

3. **Claude Code has `SupportsForking = false`.** It does not natively produce "task" tool calls. Any delegation UX for Claude Code must be Fleet-initiated (Fleet creates the child session and emits the delegation event itself).

4. **Harness abstraction is clean.** `IHarness` / `IHarnessInstance` + `HarnessEvent` + `HarnessCapabilities` are generic. The mappers (`OpenCodeMapper`, `ClaudeCodeMapper`) already normalize provider-specific events into `HarnessEvent`. The `HarnessEventRelay` broadcasts these over `IEventBroadcaster` with session-ID rewriting. This is the correct extension path.

5. **The `isTaskToolCall` / `getTaskToolInput` / `getTaskToolSessionId` helpers in `api-types.ts` are OpenCode-specific extraction functions.** They parse OpenCode's `task` tool state shape. These must be replaced with generic delegation-aware logic.

6. **`SessionCallbackService` already fires completion prompts to the parent.** The only gap is *notifying the UI* that a delegation's status changed, and *correlating* the delegation with the tool call part that spawned it.

---

## Architecture Decision: First-Class Domain Concept

**Recommendation: Model delegation as a first-class Fleet concept, NOT inferred from harness message parts.**

Rationale:
- **Inference is fragile.** Today the frontend parses OpenCode's `task` tool state. If OpenCode changes its tool schema, or a new harness uses a different tool name, the frontend breaks. The UI should never parse provider-specific payloads.
- **Fleet already owns the parent/child relationship.** `Session.ParentSessionId` and `SessionCallback` exist. A `Delegation` entity is the natural bridge between "the tool call part in the parent" and "the child session."
- **Non-forking harnesses need it.** Claude Code can't create child sessions natively. Fleet must create them on behalf of the user. A first-class concept lets Fleet orchestrate delegation for *any* harness, not just those that support it natively.
- **Event-driven UI updates are cleaner.** A `delegation.updated` broadcast event keyed by Fleet delegation ID lets the card update in real time without the frontend scraping tool call metadata.

---

## Objectives

### Core Objective
A delegation card appears in the parent session's activity stream showing the child session's title, status (pending → running → completed → error), and a link to navigate to the child session — all without the frontend knowing which harness is running.

### Deliverables
- [ ] `Delegation` domain entity and DB table
- [ ] `DelegationService` in Application layer
- [ ] Harness mapper hooks that emit `delegation.requested` events
- [ ] `HarnessEventRelay` intercept to create/update delegations
- [ ] Broadcast events: `delegation.created`, `delegation.updated`
- [ ] `DelegationCard` React component consuming only Fleet types
- [ ] `SupportsTaskDelegation` capability flag
- [ ] Deprecate / remove OpenCode-specific `isTaskToolCall` path

### Definition of Done
- [ ] Creating a child session via OpenCode's task tool renders a delegation card in the parent
- [ ] Claude Code sessions show delegation UI only when Fleet-initiated delegation is used
- [ ] No import of `@opencode-ai/sdk` or harness-specific types in any frontend component
- [ ] `IHarness.cs` is unchanged (no new methods or properties)
- [ ] All existing tests pass; new tests cover delegation lifecycle

### Guardrails (Must NOT)
- Must NOT add methods, properties, or delegation-specific types to `IHarness` or `IHarnessInstance`
- Must NOT expose harness instance IDs, resume tokens, or provider-specific session IDs to the frontend
- Must NOT make the frontend parse tool call state to determine if something is a delegation
- Must NOT break existing session creation/callback flows

---

## Canonical Event/State Model

### Delegation Entity (Domain)

```
Delegation
├── Id: string (Fleet GUID)
├── ParentSessionId: string (FK → sessions.id)
├── ChildSessionId: string? (FK → sessions.id, null until child is created)
├── ParentToolCallId: string? (correlates to ToolUsePart.ToolCallId in parent stream)
├── Title: string (e.g. "Code Review", "Write Tests")
├── Description: string? (from task input)
├── Status: "pending" | "running" | "completed" | "error" | "cancelled"
├── CreatedAt: DateTimeOffset
├── UpdatedAt: DateTimeOffset
└── CompletedAt: DateTimeOffset?
```

### Delegation Lifecycle

```
         ┌─────────────────────────────────────────────────────┐
         │  Harness emits tool call with delegation semantics   │
         │  (OpenCode: tool="task", Claude: Fleet-initiated)    │
         └──────────────────────┬──────────────────────────────┘
                                │
                                ▼
                   ┌────────────────────┐
                   │    PENDING          │  delegation.created broadcast
                   │ (child not yet up)  │
                   └────────┬───────────┘
                            │ child session created
                            ▼
                   ┌────────────────────┐
                   │    RUNNING          │  delegation.updated broadcast
                   │ (child is active)   │
                   └────────┬───────────┘
                            │ child session stops
                            ▼
                   ┌────────────────────┐
                   │    COMPLETED        │  delegation.updated broadcast
                   │    or ERROR         │  + SessionCallback fires
                   └────────────────────┘
```

### Broadcast Events (harness-agnostic, session-scoped)

```json
// Topic: "session:{parentSessionId}"
{
  "type": "delegation.created",
  "properties": {
    "delegationId": "...",
    "parentSessionId": "...",
    "childSessionId": null,
    "parentToolCallId": "tool-call-abc",
    "title": "Code Review",
    "description": "Review the auth module changes",
    "status": "pending"
  }
}

{
  "type": "delegation.updated",
  "properties": {
    "delegationId": "...",
    "childSessionId": "fleet-session-xyz",
    "status": "running"    // or "completed" / "error"
  }
}
```

### Frontend Accumulated Type

```typescript
// Replaces isTaskToolCall/getTaskToolInput/getTaskToolSessionId
interface DelegationCard {
  delegationId: string;
  parentToolCallId: string | null;
  childSessionId: string | null;
  title: string;
  description: string | null;
  status: "pending" | "running" | "completed" | "error" | "cancelled";
}
```

The activity stream accumulates these alongside messages. When a `delegation.created` event arrives, a card is inserted into the stream at the position of the corresponding `parentToolCallId` (or appended if no correlation). When `delegation.updated` arrives, the card's status and childSessionId are patched.

---

## IHarness.cs: What SHOULD and SHOULD NOT Change

### SHOULD NOT be added to IHarness / IHarnessInstance
- No `CreateDelegationAsync` or `GetDelegationsAsync` methods
- No `DelegationEvent` type on the harness interface
- No `OnDelegationRequested` callback or event
- No delegation-specific capabilities beyond a boolean flag

### SHOULD be added
- **`HarnessCapabilities.SupportsTaskDelegation`** (boolean) — tells the UI whether to show "Delegate task" affordances. OpenCode: `true`. Claude Code: `false` (initially; `true` once Fleet-initiated delegation ships).

This is the *only* change to harness-facing types. Everything else lives in the Application/Infrastructure layers.

### WHERE delegation logic lives
- **Mapper layer** (Infrastructure): `OpenCodeMapper` gains a `TryExtractDelegation(HarnessEvent)` method that detects `tool="task"` events and returns a `DelegationRequest` DTO. This is the *only* place that knows OpenCode's tool schema.
- **HarnessEventRelay** (Infrastructure): After broadcasting the raw event, checks if the mapper detected a delegation and calls `DelegationService.CreateAsync()`.
- **DelegationService** (Application): Pure domain logic for CRUD + status transitions + broadcasting `delegation.*` events.
- **SessionOrchestrator** (Application): When a child session stops, calls `DelegationService.CompleteAsync()` in addition to `SessionCallbackService.TryFireCallbacksAsync()`.

---

## Non-Forking Harnesses (Claude Code et al.)

Claude Code sets `SupportsForking = false` and `SupportsTaskDelegation = false`.

**Immediate behavior (MVP):** No delegation card appears. Claude Code sessions operate as single-agent sessions. This is correct — Claude Code's CLI does not natively fork sub-agents.

**Future behavior (post-MVP):** Fleet adds a "Delegate Task" button in the prompt bar (gated on a new `SupportsFleetDelegation` capability or a Fleet-level setting independent of harness). When clicked:
1. Fleet creates a new child session via `SessionOrchestrator.CreateSessionAsync` with `ParentSessionId` set.
2. Fleet creates a `Delegation` record with `Status = "running"`.
3. Fleet registers a `SessionCallback` so the parent is notified on completion.
4. A `delegation.created` event is broadcast to the parent's topic.

This is entirely Fleet-orchestrated — the harness just receives the completion prompt via the existing callback mechanism. No harness changes needed.

---

## Minimal Viable Design vs Ideal Design

### MVP (Ship First)

| Layer | Change | Notes |
|-------|--------|-------|
| **Domain** | `Delegation` entity | 6 fields, simple value object |
| **Domain** | `IDelegationRepository` | Insert, GetByParent, UpdateStatus |
| **Infrastructure** | Migration `006_add_delegations_table.sql` | Single table, FK to sessions |
| **Infrastructure** | Dapper `DelegationRepository` | ~80 lines |
| **Application** | `DelegationService` | Create, Update, Query |
| **Infrastructure** | `OpenCodeMapper.TryExtractDelegation()` | Detect `tool="task"` events → `DelegationRequest` |
| **Infrastructure** | `HarnessEventRelay` intercept | After broadcast, check for delegation → create record + broadcast `delegation.created` |
| **Application** | `SessionOrchestrator` | On child stop, update delegation status + broadcast `delegation.updated` |
| **API** | `GET /api/sessions/{id}/delegations` | List delegations for a session |
| **Frontend** | `delegation-state.ts` | Accumulate `delegation.*` events into `DelegationCard[]` |
| **Frontend** | `DelegationCard.tsx` component | Status icon + title + link to child |
| **Frontend** | `activity-stream-v1.tsx` | Replace `TaskDelegationItem` with `DelegationCard` |
| **Frontend** | Delete `isTaskToolCall` / `getTaskToolInput` / `getTaskToolSessionId` | Replaced by event-driven model |

### Ideal (Post-MVP)

| Layer | Change | Notes |
|-------|--------|-------|
| **Frontend** | "Delegate Task" button | Fleet-initiated delegation for any harness |
| **Application** | `DelegationOrchestrator` | Coordinates create-child + create-delegation + register-callback in one transaction |
| **HarnessCapabilities** | `SupportsFleetDelegation` | Separate from `SupportsTaskDelegation` (harness-native vs Fleet-orchestrated) |
| **Frontend** | Delegation panel in sidebar | Shows all active delegations across sessions |
| **Frontend** | Breadcrumb navigation | Parent → Child → Grandchild hierarchy |
| **API** | `POST /api/sessions/{id}/delegate` | Single endpoint for Fleet-initiated delegation |

---

## TODOs

- [ ] 1. **Add Delegation domain entity and repository interface**
  **What**: Create `Delegation` entity in Domain/Entities and `IDelegationRepository` in Domain/Repositories. The entity models the lifecycle of a parent-to-child task delegation with status tracking.
  **Files**: `src/WeaveFleet.Domain/Entities/Delegation.cs`, `src/WeaveFleet.Domain/Repositories/IDelegationRepository.cs`
  **Acceptance**: Entity compiles with Id, ParentSessionId, ChildSessionId?, ParentToolCallId?, Title, Description?, Status, CreatedAt, UpdatedAt, CompletedAt? properties. Repository interface has InsertAsync, GetByParentSessionIdAsync, UpdateStatusAsync, GetByIdAsync methods.

- [ ] 2. **Add database migration for delegations table**
  **What**: Create migration `006_add_delegations_table.sql` as an embedded resource. Table schema mirrors the entity with FKs to sessions, indexes on parent_session_id and status.
  **Files**: `src/WeaveFleet.Infrastructure/Migrations/006_add_delegations_table.sql`
  **Acceptance**: Migration creates `delegations` table with correct columns, FKs, and indexes. Applies cleanly on existing databases.

- [ ] 3. **Implement DelegationRepository (Dapper)**
  **What**: Dapper-backed implementation of `IDelegationRepository` following the patterns in existing repositories (e.g., `SessionCallbackRepository`).
  **Files**: `src/WeaveFleet.Infrastructure/Data/DelegationRepository.cs`, `src/WeaveFleet.Infrastructure/DependencyInjection.cs` (register in DI)
  **Acceptance**: All CRUD methods implemented. Registered as scoped in DI container.

- [ ] 4. **Add DelegationService to Application layer**
  **What**: Business logic for delegation lifecycle: `CreateAsync` (inserts record + broadcasts `delegation.created`), `UpdateStatusAsync` (transitions status + broadcasts `delegation.updated`), `GetForSessionAsync` (lists delegations for a parent session). Uses `IEventBroadcaster` for real-time updates.
  **Files**: `src/WeaveFleet.Application/Services/DelegationService.cs`
  **Acceptance**: Service creates delegations, validates status transitions (pending → running → completed/error/cancelled), broadcasts events on topic `session:{parentSessionId}`.

- [ ] 5. **Add SupportsTaskDelegation to HarnessCapabilities**
  **What**: Add `bool SupportsTaskDelegation` property to `HarnessCapabilities`. Set to `true` in `OpenCodeHarness`, `false` in `ClaudeCodeHarness`. Update frontend `HarnessCapabilities` type.
  **Files**: `src/WeaveFleet.Domain/Harnesses/HarnessTypes.cs`, `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeHarness.cs`, `src/WeaveFleet.Infrastructure/Harnesses/ClaudeCode/ClaudeCodeHarness.cs`, `client/src/lib/api-types.ts`
  **Acceptance**: Capability is serialized in `GET /api/harnesses` response. Frontend type includes `supportsTaskDelegation: boolean`.

- [ ] 6. **Add TryExtractDelegation to OpenCodeMapper**
  **What**: Static method that inspects a `HarnessEvent` and, if it represents a `tool="task"` call (from `message.part.updated` events), extracts delegation metadata into a `DelegationRequest` record. This encapsulates all OpenCode-specific task-tool knowledge in the mapper.
  **Files**: `src/WeaveFleet.Infrastructure/Harnesses/OpenCode/OpenCodeMapper.cs`
  **Acceptance**: Returns non-null `DelegationRequest` for task tool events with `subagent_type` and `description` in input. Returns null for all other events. Unit tests verify extraction from contract fixtures.

- [ ] 7. **Wire delegation detection into HarnessEventRelay**
  **What**: After broadcasting the raw event, call `OpenCodeMapper.TryExtractDelegation()`. If a delegation is detected: (a) look up existing delegation by `parentToolCallId` — if none, create via `DelegationService.CreateAsync()`; (b) if the tool state is `completed` or `error`, call `DelegationService.UpdateStatusAsync()`. Must use `IServiceScopeFactory` for scoped services.
  **Files**: `src/WeaveFleet.Infrastructure/Services/HarnessEventRelay.cs`
  **Acceptance**: Delegation records are created in the DB when OpenCode emits task tool events. `delegation.created` and `delegation.updated` events appear on the parent session's WebSocket topic.

- [ ] 8. **Update SessionOrchestrator to set delegation status on child stop**
  **What**: In `DeleteSessionAsync` and in the session-stop lifecycle path, after firing callbacks, check if the stopped session is the child of any delegation. If so, update the delegation status to `completed` (or `error` if the session errored). Broadcast `delegation.updated`.
  **Files**: `src/WeaveFleet.Application/Services/SessionOrchestrator.cs`
  **Acceptance**: When a child session stops, its parent's delegation card transitions to completed/error.

- [ ] 9. **Add delegation API endpoint**
  **What**: `GET /api/sessions/{id}/delegations` returns the list of `Delegation` records for a parent session. Used by the frontend on initial load to hydrate delegation cards.
  **Files**: `src/WeaveFleet.Api/Endpoints/SessionEndpoints.cs`
  **Acceptance**: Returns 200 with delegation array. Returns 404 if session not found.

- [ ] 10. **Add frontend delegation state accumulator**
  **What**: New module `delegation-state.ts` with pure functions: `applyDelegationCreated(prev, event)` and `applyDelegationUpdated(prev, event)` that maintain a `DelegationCard[]`. Follow the pattern of `event-state.ts`.
  **Files**: `client/src/lib/delegation-state.ts`
  **Acceptance**: Pure functions with unit tests. No React dependencies. Handles idempotent reapplication.

- [ ] 11. **Wire delegation events into useSessionEvents**
  **What**: Handle `delegation.created` and `delegation.updated` event types in the SSE handler. Maintain a `delegations: DelegationCard[]` state alongside `messages`. Load initial delegations from `GET /api/sessions/{id}/delegations` on mount.
  **Files**: `client/src/hooks/use-session-events.ts`
  **Acceptance**: Delegation cards are populated on mount and updated in real time via WebSocket.

- [ ] 12. **Create DelegationCard React component**
  **What**: Stateless component rendering a delegation: status icon (spinner/check/error), title, description, and a `<Link>` to the child session. Follows the visual pattern of the existing `TaskDelegationItem` but consumes only `DelegationCard` props (no tool-state parsing). Positioned inline in the activity stream.
  **Files**: `client/src/components/session/delegation-card.tsx`
  **Acceptance**: Renders correctly for all status values. Link navigates to `/sessions/{childSessionId}?parentSessionId={parentSessionId}`. No import of harness-specific types.

- [ ] 13. **Integrate DelegationCard into activity stream**
  **What**: In `activity-stream-v1.tsx`, replace the `TaskDelegationItem` rendering path with `DelegationCard`. Delegations are rendered at the position of their `parentToolCallId` (if matched) or at the end of the stream. Remove the `isTaskToolCall` guard from `ToolCallItem`.
  **Files**: `client/src/components/session/activity-stream-v1.tsx`
  **Acceptance**: Delegation cards appear in the correct position. No OpenCode-specific parsing remains in the component.

- [ ] 14. **Deprecate OpenCode-specific task tool helpers**
  **What**: Remove or deprecate `isTaskToolCall`, `getTaskToolInput`, `getTaskToolSessionId` from `api-types.ts`. These are replaced by the delegation event model. If any other code still references them, update those call sites.
  **Files**: `client/src/lib/api-types.ts`, `client/src/components/session/activity-stream-v1.tsx`
  **Acceptance**: No remaining references to these functions. `api-types.ts` no longer contains OpenCode-specific task tool parsing logic.

- [ ] 15. **Add isRelevantToSession support for delegation events**
  **What**: Update `isRelevantToSession` in `event-state.ts` to recognize `delegation.created` and `delegation.updated` events and forward them to the correct session.
  **Files**: `client/src/lib/event-state.ts`
  **Acceptance**: Delegation events are forwarded when `properties.parentSessionId === sessionId`.

---

## Migration / Rollout Strategy

1. **Backend first.** Ship tasks 1–9 behind feature flag or simply as new code paths. Existing `TaskDelegationItem` continues working because the old `message.part.updated` events still flow. No breaking change.

2. **Frontend dual-path.** During migration, `activity-stream-v1.tsx` checks for `DelegationCard` data first; falls back to the existing `isTaskToolCall` path. This lets the backend and frontend ship independently.

3. **Deprecation.** Once the delegation event path is validated in production, remove the `isTaskToolCall` fallback path.

4. **DB migration.** The new `delegations` table is additive — no schema changes to existing tables. Applied automatically by `MigrationRunner` on startup.

---

## Top Risks

| Risk | Mitigation |
|------|-----------|
| **Race condition: tool event arrives before child session is created** | Delegation starts in `pending` status. `HarnessEventRelay` creates the delegation on the first tool event; a subsequent event or session-creation callback updates it to `running` with the `childSessionId`. |
| **OpenCode changes its task tool schema** | Only `OpenCodeMapper.TryExtractDelegation()` parses it. Schema changes are isolated to one method. Add contract tests against fixture JSON. |
| **Multiple harness events for the same delegation** | Deduplicate by `parentToolCallId` — if a delegation already exists for that tool call, update rather than create. The `DelegationService` enforces idempotency. |
| **Claude Code users see no delegation UI** | Expected and correct for MVP. Document that delegation requires `SupportsTaskDelegation = true`. Future work adds Fleet-initiated delegation. |
| **Performance: extra DB writes per tool event** | `TryExtractDelegation` returns null for 99%+ of events (only `tool="task"` triggers it). The delegation check is a cheap string comparison, not a DB query. |
| **Frontend cache invalidation** | Delegations are loaded from API on mount and updated via WebSocket. The `sessionCache` mechanism in `use-session-events.ts` needs to include delegation state in its snapshot — add `delegations` to the cache entry type. |

---

## Verification

- [ ] All existing tests pass (`dotnet test`, `bun test`)
- [ ] New unit tests: `DelegationService` lifecycle transitions, `OpenCodeMapper.TryExtractDelegation` extraction, `delegation-state.ts` accumulator
- [ ] Integration test: create session with task tool → delegation record created → child session stops → delegation marked completed
- [ ] Manual verification: delegation card renders in parent stream, links to child, status updates in real time
- [ ] `IHarness.cs` diff is zero lines (unchanged)
- [ ] No `@opencode-ai/sdk` imports in `client/src/components/` or `client/src/hooks/`
- [ ] Release build passes (`dotnet build -c Release`)
