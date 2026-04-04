# Normalize Session ID Across Frontend and Backend

## TL;DR
> **Summary**: Change `session.id` in the API list response to be the Fleet DB primary key instead of the harness-specific OpenCode session ID, then remove all `dbId` workarounds from the frontend.
> **Estimated Effort**: Medium

## Context

### Original Request
The frontend receives two IDs per session: `session.id` (set to the OpenCode session ID, a harness implementation detail) and `dbId` (the Fleet DB primary key). The frontend uses `session.id` everywhere (routing, React keys, lookups), but mutation endpoints (DELETE, PATCH, POST) need the DB ID. This caused 404s and required `dbId ?? session.id` workarounds scattered across 10+ files.

### Key Findings

**Root cause**: A single line in `SessionEndpoints.cs` (line 222):
```csharp
Session: new SessionFleetInfo(
    Id: s.OpencodeSessionId,  // ← This should be s.Id
    ...
```

**The mismatch**: The list endpoint sets `SessionFleetInfo.Id` to `Session.OpencodeSessionId` (the harness instance ID), but ALL other endpoints (create, resume, fork, messages, events, status, delete, abort, prompt) use `Session.Id` (the DB primary key). This creates an inconsistency where:
- Navigating from the session list → URL contains `OpencodeSessionId`
- `GET /api/sessions/{id}` queries `WHERE id = @Id` (DB primary key) → would 404 if `OpencodeSessionId ≠ Id`
- Mutation endpoints look up by DB primary key → need `dbId` fallback

**What already works correctly**:
- `POST /api/sessions` (create) → returns `Session` entity with `Session.Id` = GUID ✓
- `POST /api/sessions/{id}/resume` → returns `Session` entity with `Session.Id` ✓
- `POST /api/sessions/{id}/fork` → creates via `CreateSessionAsync` which returns `Session.Id` ✓
- `GET /api/sessions/{id}` → queries by `Session.Id` (DB PK) ✓
- `GET /api/sessions/{id}/events` → queries by `Session.Id` (DB PK) ✓
- All mutation endpoints → accept DB primary key ✓

**The SSE event pipeline**: `SessionEventEndpoints.cs` (line 24) does `sessionRepo.GetByIdAsync(id)` which queries by DB primary key. After the fix, the URL will contain the DB ID, so this works correctly. The `evt.SessionId` in the streamed events (line 45) comes from the harness and may still be the harness session ID — this is internal to the event stream and doesn't affect the fix.

**Ancestors**: The `AncestorInfo` interface in `_page-client.tsx` (line 46-50) has `dbId`, `instanceId`, and `title`. Currently the `GET /api/sessions/{id}` endpoint returns the raw `Session` entity which does **not** include `ancestors`. The `ancestors` field appears to be expected but not yet implemented on the backend — the frontend handles `data.ancestors` being `undefined` gracefully. When ancestors ARE implemented, they should use the DB ID (which is what `parentSessionId` already stores). The `AncestorInfo.dbId` property name should be renamed to just `id` for consistency, but since the backend doesn't send this data yet, this is a naming concern for future implementation.

**Parent-child matching**: `session-utils.ts` uses `dbId` to build a parent lookup map. `parentSessionId` in the DB stores the parent's DB primary key. After the fix, `session.id` will be the DB ID, making `dbId` redundant — the nesting logic can use `session.id` directly.

## Objectives

### Core Objective
Make `session.id` in ALL API responses carry the Fleet DB primary key. Remove the `dbId` field and all frontend workarounds.

### Deliverables
- [x] Backend: Fix list endpoint to use `s.Id` instead of `s.OpencodeSessionId` for `SessionFleetInfo.Id`
- [x] Backend: Remove `DbId` from `SessionListResponse` DTO
- [x] Frontend: Remove `dbId` from `SessionListItem` type
- [x] Frontend: Remove all `dbId ?? session.id` fallback patterns (10+ locations)
- [x] Frontend: Update `session-utils.ts` nesting logic to use `session.id`
- [x] Frontend: Update `_page-client.tsx` ancestor links to use `id` instead of `dbId`
- [x] Update all affected tests

### Definition of Done
- [x] `dotnet test` passes with no failures
- [x] `npm run typecheck` passes in client/
- [x] `npm test` passes in client/
- [x] No occurrence of `dbId` in the codebase (search: `grep -r "dbId" client/src/ src/`)
- [x] The response from `GET /api/sessions` contains `session.id` = DB primary key and no `dbId` field

### Guardrails (Must NOT)
- Must NOT change the `Session.Id` or `Session.OpencodeSessionId` domain entity properties
- Must NOT change how the backend resolves session ID → harness instance (this mapping stays internal)
- Must NOT change the `instanceId` field or query parameter — it remains separate
- Must NOT modify the DB schema

## TODOs

### Backend Changes

- [x] 1. **Fix `SessionFleetInfo.Id` in list endpoint**
  **What**: Change `Id: s.OpencodeSessionId` to `Id: s.Id` in the `ToListResponse` method.
  **Files**: `src/WeaveFleet.Api/Endpoints/SessionEndpoints.cs` (line 222)
  **Change**:
  ```csharp
  // Before
  Session: new SessionFleetInfo(
      Id: s.OpencodeSessionId,
  // After
  Session: new SessionFleetInfo(
      Id: s.Id,
  ```
  **Acceptance**: The JSON response for `GET /api/sessions` has `session.id` matching the DB primary key, not the harness ID.

- [x] 2. **Remove `DbId` from `SessionListResponse` DTO**
  **What**: Remove the `DbId` parameter from the `SessionListResponse` record. It's now redundant since `SessionFleetInfo.Id` carries the same value.
  **Files**: `src/WeaveFleet.Application/DTOs/SessionDtos.cs` (line 15)
  **Change**: Remove `string? DbId,` from the record parameters.
  **Acceptance**: The DTO compiles without `DbId`. The JSON response no longer includes `dbId`.

- [x] 3. **Remove `DbId` from `ToListResponse` mapping**
  **What**: Remove the `DbId: s.Id` argument from the `ToListResponse` method call.
  **Files**: `src/WeaveFleet.Api/Endpoints/SessionEndpoints.cs` (line 226)
  **Change**: Remove `DbId: s.Id,` from the `SessionListResponse` constructor call.
  **Acceptance**: Compiles cleanly. No `DbId` in the response.

- [x] 4. **Verify create/resume/fork responses**
  **What**: Verify these endpoints already return `Session.Id` (DB PK) — no changes expected.
  **Files**:
  - `src/WeaveFleet.Api/Endpoints/SessionEndpoints.cs` lines 56-63 (create), 90-91 (resume), 100-107 (fork)
  - `src/WeaveFleet.Application/Services/SessionOrchestrator.cs` lines 96-107 (session construction)
  **Acceptance**: Confirm `CreateSessionResult.Session.Id` = GUID (DB PK). The frontend `FleetSession.id` from create/resume/fork will be the DB primary key.

- [x] 5. **Verify SSE event endpoint**
  **What**: Confirm `GET /api/sessions/{id}/events` already queries by DB primary key (`sessionRepo.GetByIdAsync(id)`).
  **Files**: `src/WeaveFleet.Api/Endpoints/SessionEventEndpoints.cs` (line 24)
  **Acceptance**: No changes needed. The event stream subscription uses `session.InstanceId` to find the live harness instance, which is correct.

- [x] 6. **Verify all `{id}` path parameter endpoints**
  **What**: All endpoints that accept `{id}` in the path should resolve sessions by DB primary key. Verify: prompt (line 68), abort (line 79), resume (line 87), fork (line 97), messages (line 113), diffs (line 139), status (line 144), command (line 163), delete (line 168), rename (line 182), move-to-project (line 190).
  **Files**: `src/WeaveFleet.Api/Endpoints/SessionEndpoints.cs`
  **Acceptance**: All endpoints use `sessionRepository.GetByIdAsync(id)` or `orchestrator.*Async(id)` which internally does the same. No changes needed.

### Frontend Changes

- [x] 7. **Remove `dbId` from `SessionListItem` type**
  **What**: Remove `dbId?: string` from the `SessionListItem` interface.
  **Files**: `client/src/lib/api-types.ts` (line 118)
  **Change**: Delete lines 117-118 (`/** Internal Fleet DB session ID... */ dbId?: string;`).
  **Acceptance**: TypeScript compilation passes. No `dbId` on the interface.

- [x] 8. **Simplify `sidebar-session-item.tsx` — remove dbId fallbacks**
  **What**: Replace all `item.dbId ?? item.session.id` with `item.session.id`. Remove `item.dbId` from dependency arrays.
  **Files**: `client/src/components/layout/sidebar-session-item.tsx`
  **Locations** (6 occurrences):
  - Line 81: `const dbId = item.dbId ?? item.session.id;` → use `item.session.id` directly
  - Line 89: dependency array `[item.dbId, item.session.id, ...]` → `[item.session.id, ...]`
  - Line 94: `const dbId = item.dbId ?? item.session.id;` → use `item.session.id`
  - Line 100: dependency array `[..., item.dbId, item.session.id, ...]` → `[..., item.session.id, ...]`
  - Line 104/109/113/121: same pattern for abort, delete handlers
  - Line 266: `sourceSessionId={item.dbId ?? item.session.id}` → `sourceSessionId={item.session.id}`
  **Acceptance**: No references to `dbId` remain. All actions use `session.id`.

- [x] 9. **Simplify `live-session-card.tsx` — remove dbId fallbacks**
  **What**: Replace all `item.dbId ?? session.id` with `session.id`.
  **Files**: `client/src/components/fleet/live-session-card.tsx`
  **Locations** (4 occurrences):
  - Line 161: `onTerminate(item.dbId ?? session.id, instanceId)` → `onTerminate(session.id, instanceId)`
  - Line 176: `onAbort!(item.dbId ?? session.id, instanceId)` → `onAbort!(session.id, instanceId)`
  - Line 191: `onDelete!(item.dbId ?? session.id, instanceId)` → `onDelete!(session.id, instanceId)`
  - Line 211: `onResume(item.dbId ?? session.id)` → `onResume(session.id)`
  **Acceptance**: No references to `dbId` remain.

- [x] 10. **Simplify `session-group.tsx` — remove dbId fallback**
  **What**: Replace `s.dbId ?? s.session.id` with `s.session.id`.
  **Files**: `client/src/components/fleet/session-group.tsx`
  **Location**: Line 84
  **Acceptance**: No references to `dbId` remain.

- [x] 11. **Simplify `sidebar-workspace-item.tsx` — remove dbId fallback**
  **What**: Replace `s.dbId ?? s.session.id` with `s.session.id`.
  **Files**: `client/src/components/layout/sidebar-workspace-item.tsx`
  **Location**: Line 91
  **Acceptance**: No references to `dbId` remain.

- [x] 12. **Simplify `session-commands.tsx` — remove dbId fallback**
  **What**: Replace `s.dbId ?? s.session.id` with `s.session.id`.
  **Files**: `client/src/components/commands/session-commands.tsx`
  **Location**: Line 26
  **Acceptance**: No references to `dbId` remain.

- [x] 13. **Simplify `_page-client.tsx` — remove dbId derivation and fallbacks**
  **What**: Remove the `dbId` local variable and replace all uses with `sessionId` (which comes from the URL and will now be the DB ID).
  **Files**: `client/src/app/sessions/[id]/_page-client.tsx`
  **Locations**:
  - Line 47: Remove `dbId: string;` from `AncestorInfo` interface → rename to `id: string`
  - Line 84: Remove `const dbId = contextMatch?.dbId ?? sessionId;` — use `sessionId` directly
  - Lines 155, 163: Replace `dbId` → `sessionId` in abort command
  - Lines 489, 496: Replace `dbId` → `sessionId` in handleStop
  - Lines 504, 511: Replace `dbId` → `sessionId` in handleAbort
  - Lines 522, 529: Replace `dbId` → `sessionId` in handleResume
  - Lines 533, 538: Replace `dbId` → `sessionId` in handlePermanentDelete
  - Line 742: `parent.dbId` → `parent.id` in ancestor link
  - Line 750: `ancestor.dbId` → `ancestor.id` as React key
  - Line 753: `ancestor.dbId` → `ancestor.id` in ancestor link
  **Acceptance**: No `dbId` references remain. All mutation endpoints use `sessionId` from the URL.

- [x] 14. **Simplify `page.tsx` — remove dbId fallback in delete lookup**
  **What**: Simplify the `handleDeleteRequest` session lookup.
  **Files**: `client/src/app/page.tsx`
  **Location**: Line 93
  **Change**: `sessions.find((s) => s.dbId === sessionId || s.session.id === sessionId)` → `sessions.find((s) => s.session.id === sessionId)`
  **Acceptance**: Lookup uses only `session.id`.

- [x] 15. **Update `session-utils.ts` — replace dbId with session.id for nesting**
  **What**: The `nestSessions` function uses `s.dbId` to build parent→child maps. Since `session.id` now IS the DB ID, use `s.session.id` instead.
  **Files**: `client/src/lib/session-utils.ts`
  **Locations**:
  - Line 54: `const dbIdMap = new Map(...)` → rename to `sessionIdMap`
  - Line 56: `if (s.dbId) dbIdMap.set(s.dbId, s)` → `sessionIdMap.set(s.session.id, s)`
  - Line 63: `dbIdMap.has(s.parentSessionId)` → `sessionIdMap.has(s.parentSessionId)`
  - Line 76: `s.dbId ? (childrenByParent.get(s.dbId) ?? []) : []` → `childrenByParent.get(s.session.id) ?? []`
  - Update comments on lines 45-47 to reflect the new approach
  **Acceptance**: No references to `dbId` remain. Parent-child nesting still works correctly.

- [x] 16. **Update `session-utils.test.ts` — remove dbId from test fixtures**
  **What**: Update all test helper `makeItem` calls and assertions to not use `dbId`. The `dbId` field will no longer exist. Parent-child tests should set `session.id` to the value that `parentSessionId` references.
  **Files**: `client/src/lib/__tests__/session-utils.test.ts`
  **Changes**:
  - Line 21: Remove `dbId: undefined` from `makeItem` default
  - All test cases that set `dbId: "db-xxx"` should instead set the `session.id` to `"db-xxx"` (since `session.id` is now the DB ID)
  - Lines 52-53: `makeItem({ sessionId: "parent", dbId: "db-parent" })` → `makeItem({ sessionId: "db-parent" })`
  - And child's `parentSessionId: "db-parent"` stays the same
  - Similarly for all other test cases
  - Line 111-114: "SessionWithoutDbIdCannotBeParent" test can be removed (every session now has `session.id` which serves as the DB ID)
  **Acceptance**: All tests pass. No `dbId` in test code.

- [x] 17. **Update `sessions-context.test.ts` — verify no dbId usage**
  **What**: Check that the `makeSession` helper and tests don't reference `dbId`. Currently they don't set `dbId` explicitly (it's not in the helper).
  **Files**: `client/src/contexts/__tests__/sessions-context.test.ts`
  **Acceptance**: Tests pass without modification (verify only).

## Verification

- [x] Backend builds: `dotnet build` succeeds
- [x] Backend tests pass: `dotnet test`
- [x] Frontend type-checks: `npm run typecheck` in `client/`
- [x] Frontend tests pass: `npm test` in `client/`
- [x] No `dbId` references remain: `grep -r "dbId\|DbId" client/src/ src/WeaveFleet.Api/ src/WeaveFleet.Application/DTOs/` returns nothing
- [x] Manual smoke test: List sessions → click one → URL uses DB ID → mutations (stop, abort, resume, delete) work without 404
- [x] Manual smoke test: Create session → navigate to it → session ID in URL matches DB primary key
- [x] Manual smoke test: Parent-child sessions render correctly in sidebar (nesting works)
- [x] Manual smoke test: Fork session dialog works, navigates to forked session correctly

## Risks and Mitigations

| Risk | Likelihood | Mitigation |
|------|-----------|------------|
| External bookmarks with OpenCode session IDs break | Low (internal tool) | Accept — no external consumers. Old URLs were already broken for `GET /api/sessions/{id}` since that endpoint queries by DB PK. |
| SSE event `sessionId` field still uses harness ID | None — acceptable | The SSE event `sessionId` in `evt.SessionId` (from `SessionEventEndpoints.cs` line 45) comes from the harness event stream. This is internal to the event processing pipeline and doesn't affect the frontend's concept of session identity. The frontend subscribes to events via `/api/sessions/{id}/events` where `{id}` is the DB PK (resolved correctly). |
| `ResumeSessionResponse` missing `instanceId` at top level | Pre-existing | The resume endpoint returns `{ session }` but the frontend type expects `{ instanceId, session }`. This is a separate pre-existing issue. The `session` object includes `instanceId` as a property. This plan does not address this. |
| Ancestor links break | None | The backend doesn't send `ancestors` data yet (field is always undefined). When implemented, it should use DB IDs. The `AncestorInfo` interface rename from `dbId` to `id` prepares for correct future implementation. |

## Implementation Order

1. **Backend first** (TODOs 1–3): Fix the list endpoint and DTO. This is the source-of-truth change.
2. **Backend verification** (TODOs 4–6): Read-only verification, no code changes.
3. **Frontend types** (TODO 7): Remove `dbId` from the type — this will surface all locations that need updating via TypeScript errors.
4. **Frontend components** (TODOs 8–14): Fix each component. These can be done in any order since they're independent.
5. **Frontend utilities** (TODO 15): Update the nesting logic.
6. **Frontend tests** (TODOs 16–17): Update test fixtures.
7. **Verification** (all checks): Run full test suite and manual smoke tests.
