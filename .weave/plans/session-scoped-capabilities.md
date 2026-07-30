# Session-Scoped Capability Endpoints

## TL;DR
Move capability queries (models, commands, agents, find/files) from `/api/instances/{id}/...` to `/api/sessions/{id}/...` so the client never needs `instanceId` for these queries. Also remove vestigial `instanceId` params from diffs/abort client calls.

## Context
- `InstanceEndpoints.cs` exposes capability queries keyed by `instanceId`. The client must resolve `instanceId` from the session list before querying capabilities.
- `SessionEndpoints.cs` already has session-scoped interaction endpoints (prompt, command, abort, messages) that use `SessionOrchestrator.GetOrActivateInstanceAsync` to resolve the live instance from a session.
- The `GetOrActivateInstanceAsync` pattern: look up session → `instanceTracker.Get(session.InstanceId)` → auto-activate if needed → return `IHarnessSession`.
- The diffs endpoint (`GET /api/sessions/{id}/diffs`) already ignores the `?instanceId=` query param. The abort endpoint also ignores it.
- Client composables (`use-models`, `use-agents`, `use-autocomplete`, `use-find-files`) currently derive `instanceId` from the sessions store, then call `/api/instances/{instanceId}/...`.

### Key files
- **Server**: `src/WeaveFleet.Api/Endpoints/SessionEndpoints.cs`, `src/WeaveFleet.Api/Endpoints/InstanceEndpoints.cs`
- **Orchestrator**: `src/WeaveFleet.Application/Services/SessionOrchestrator.cs` (line 1467: `GetOrActivateInstanceAsync`)
- **Client**: `client/src/composables/use-models.ts`, `use-agents.ts`, `use-autocomplete.ts`, `use-find-files.ts`, `use-diffs.ts`, `use-session-actions.ts`
- **Component**: `client/src/components/session/Composer.vue`

## Scope
- In scope:
  - Add 4 new session-scoped capability endpoints to `SessionEndpoints.cs`
  - Update 4 client composables to use `sessionId` instead of `instanceId`
  - Remove unused `instanceId` query params from diffs/abort client calls
  - Remove `instanceId` param from `useDiffs` signature
  - Update `useAbortSession` to not require `instanceId`
  - Mark old instance endpoints with `[Obsolete]` / XML doc deprecation
- Out of scope:
  - Removing `instanceId` from the session API response type (still needed for other purposes)
  - Removing old instance endpoints (keep for backward compat)
  - Changing the `POST /api/instances/{id}/command` endpoint (already has session-scoped equivalent)
- Constraints / assumptions:
  - New endpoints must follow the same ownership/auth pattern as existing session endpoints (orchestrator handles it)
  - Response shapes should match existing instance endpoint responses for client compatibility

## Objectives
- Client composables use only `sessionId` for capability queries
- No client code references `/api/instances/` for models, commands, agents, or file search
- Diffs and abort calls drop vestigial `instanceId` params

## Dependencies and Order
1. **Server endpoints first** — new session-scoped endpoints must exist before client can switch to them.
2. **Client composables** — can all be updated in parallel after server endpoints are deployed.
3. **Callers/components** — update signatures of composables, then update call sites.
4. **Deprecation** — mark old endpoints last, after client is fully migrated.

## Tasks

- [x] 1. Add session-scoped capability methods to `SessionOrchestrator`
  - **What**: Add 4 public methods: `GetSessionModelsAsync(sessionId, ct)`, `GetSessionCommandsAsync(sessionId, ct)`, `GetSessionAgentsAsync(sessionId, ct)`, `FindSessionFilesAsync(sessionId, query, ct)`. Each calls `GetOrActivateInstanceAsync` to resolve the instance, then delegates to the corresponding `IHarnessSession` method. For `FindSessionFiles`, get the session's `Directory` for filesystem search (mirroring the `InstanceEndpoints` logic that uses `dbInstance.Directory`).
  - **Files**: `src/WeaveFleet.Application/Services/SessionOrchestrator.cs`
  - **Depends on**: None
  - **Acceptance**:
    - Each method returns `Result<T>` following existing patterns
    - `FindSessionFilesAsync` uses the session's `Directory` property (not DB instance directory)
    - Methods compile and are accessible from endpoint layer

- [x] 2. Add session-scoped capability endpoints to `SessionEndpoints.cs`
  - **What**: Add 4 GET endpoints under the existing `/api/sessions/{id}` group:
    - `GET /models` → calls `orchestrator.GetSessionModelsAsync`, returns same shape as `InstanceEndpoints` (`List<InstanceProviderItem>`)
    - `GET /commands` → calls `orchestrator.GetSessionCommandsAsync`, returns `InstanceCommandsResponse` (but with sessionId instead of instanceId)
    - `GET /agents` → calls `orchestrator.GetSessionAgentsAsync`, returns `InstanceAgentsResponse` (but with sessionId)
    - `GET /find/files?q=` → calls `orchestrator.FindSessionFilesAsync`, returns same file list shape
  - **Files**: `src/WeaveFleet.Api/Endpoints/SessionEndpoints.cs`
  - **Depends on**: Task 1
  - **Acceptance**:
    - Endpoints return 404 when session not found
    - Endpoints auto-activate instance via orchestrator
    - Response shapes are compatible with current client parsing (arrays or wrapper objects)
    - Each has a unique `WithName` (e.g., `"GetSessionModels"`, `"GetSessionCommands"`, `"GetSessionAgents"`, `"FindSessionFiles"`)

- [x] 3. Update `use-models.ts` to use sessionId-scoped endpoint
  - **What**: Remove the `instanceId` computed property. Watch `resolvedSessionId` directly. Change fetch URL to `/api/sessions/${sessionId}/models`.
  - **Files**: `client/src/composables/use-models.ts`
  - **Depends on**: Task 2
  - **Acceptance**:
    - No reference to `instanceId` in the file
    - Watches `resolvedSessionId` and fetches when it changes
    - Existing response parsing still works (API returns same provider/model shape)

- [x] 4. Update `use-agents.ts` to use sessionId-scoped endpoint
  - **What**: Change the function signature from `useAgents(instanceId?: string)` to `useAgents(sessionId?: string)`. Resolve `sessionId` from the store (same as `use-models.ts` pattern). Change fetch URL to `/api/sessions/${sessionId}/agents`.
  - **Files**: `client/src/composables/use-agents.ts`
  - **Depends on**: Task 2
  - **Acceptance**:
    - No reference to `instanceId` in the file
    - Callers (`Composer.vue`, `use-send-command.ts`, `use-send-prompt.ts`) still compile (they call `useAgents()` with no args, which is unchanged)
    - Response parsing handles both `{ agents: [...] }` and bare array (existing logic)

- [x] 5. Update `use-find-files.ts` to accept sessionId instead of instanceId
  - **What**: Rename parameter from `instanceId` to `sessionId`. Change fetch URL to `/api/sessions/${sessionId}/find/files?q=...`. Update internal variable names.
  - **Files**: `client/src/composables/use-find-files.ts`
  - **Depends on**: Task 2
  - **Acceptance**:
    - Parameter is `sessionId`
    - Fetch URL uses `/api/sessions/`
    - Response parsing unchanged

- [x] 6. Update `use-autocomplete.ts` to pass sessionId to sub-composables
  - **What**: 
    - Rename `UseAutocompleteParams.instanceId` → `sessionId` (or add `sessionId` and remove `instanceId`)
    - Rename internal `useInstanceCommands` → `useSessionCommands`, change its fetch URL to `/api/sessions/${sessionId}/commands`
    - Rename internal `useInstanceAgents` → `useSessionAgents`, change its fetch URL to `/api/sessions/${sessionId}/agents`
    - Pass `sessionId` (instead of `instanceId`) to `useFindFiles`
  - **Files**: `client/src/composables/use-autocomplete.ts`
  - **Depends on**: Tasks 4, 5
  - **Acceptance**:
    - No reference to `instanceId` in the file
    - All 3 sub-queries use `/api/sessions/` URLs
    - Autocomplete test still passes (update test if needed)

- [x] 7. Update `Composer.vue` to pass sessionId to autocomplete
  - **What**: Change the autocomplete call to pass `sessionId: props.sessionId` instead of `instanceId: normalizedInstanceId`. Remove the `normalizedInstanceId` computed and the `autocompleteEnabled` computed (replace with sessionId-based check). The `instanceId` prop may still be needed by other parts of the component — check abort usage.
  - **Files**: `client/src/components/session/Composer.vue`
  - **Depends on**: Task 6
  - **Acceptance**:
    - Autocomplete uses `sessionId` not `instanceId`
    - Component still compiles and autocomplete triggers work

- [x] 8. Remove `instanceId` param from `useDiffs`
  - **What**: The server endpoint `GET /api/sessions/{id}/diffs` already ignores `instanceId`. Remove the `instanceId` parameter from `useDiffs()`. Remove `?instanceId=...` from the fetch URL. Remove `currentInstanceId` computed and all guards that check it. The diff fetch should only depend on `sessionId`.
  - **Files**: `client/src/composables/use-diffs.ts`
  - **Depends on**: None (server already ignores it)
  - **Acceptance**:
    - `useDiffs` takes only `sessionId` parameter
    - Fetch URL is `/api/sessions/${sessionId}/diffs` with no query params
    - Callers updated to not pass `instanceId`

- [x] 9. Remove `instanceId` param from `useAbortSession`
  - **What**: The server endpoint `POST /api/sessions/{id}/abort` already ignores `instanceId`. Change `abortSession(sessionId, instanceId)` → `abortSession(sessionId)`. Remove `?instanceId=...` from the URL. Update the `UseAbortSessionResult` interface.
  - **Files**: `client/src/composables/use-session-actions.ts`
  - **Depends on**: None (server already ignores it)
  - **Acceptance**:
    - `abortSession` takes only `sessionId`
    - No `instanceId` query param in abort URL
    - All callers updated

- [x] 10. Update callers of modified composables
  - **What**: Find and update all call sites of `useDiffs`, `useAbortSession`, and any other composables whose signatures changed. Search for `useDiffs(`, `abortSession(`, `useAgents(` with explicit args.
  - **Files**: Grep for callers — likely includes view/page components and other composables.
  - **Depends on**: Tasks 8, 9
  - **Acceptance**:
    - `bun run type-check` passes (or `bunx vue-tsc --noEmit`)
    - No TypeScript errors related to changed signatures

- [x] 11. Update autocomplete test
  - **What**: Update `client/src/composables/__tests__/use-autocomplete.test.ts` to pass `sessionId` instead of `instanceId` and mock `/api/sessions/` URLs instead of `/api/instances/`.
  - **Files**: `client/src/composables/__tests__/use-autocomplete.test.ts`
  - **Depends on**: Task 6
  - **Acceptance**:
    - Test passes with `bun run test`

- [x] 12. Update diffs test
  - **What**: Update `client/src/composables/__tests__/use-diffs.test.ts` to remove `instanceId` parameter and update mocked URLs.
  - **Files**: `client/src/composables/__tests__/use-diffs.test.ts`
  - **Depends on**: Task 8
  - **Acceptance**:
    - Test passes with `bun run test`

- [x] 13. Mark old instance endpoints as deprecated
  - **What**: Add XML doc `/// <remarks>Deprecated: use session-scoped equivalents.</remarks>` comments to the 4 capability endpoints in `InstanceEndpoints.cs`. Do not remove them yet.
  - **Files**: `src/WeaveFleet.Api/Endpoints/InstanceEndpoints.cs`
  - **Depends on**: Tasks 3–7 (client fully migrated)
  - **Acceptance**:
    - Old endpoints still functional
    - Deprecation noted in comments

- [x] 14. Verify full build and tests
  - **Depends on**: All previous tasks
  - **Acceptance**:
    - `dotnet build` passes (Release mode)
    - `cd client && bunx vue-tsc --noEmit` passes
    - `cd client && bun run test` passes
    - `dotnet test tests/WeaveFleet.Api.Tests` passes
    - Manual smoke test: open a session, verify model/agent/command dropdowns populate

## Verification
```bash
# Server build
dotnet build -c Release

# Client type check
cd client && bunx vue-tsc --noEmit

# Client tests
cd client && bun run test

# Server tests
dotnet test tests/WeaveFleet.Api.Tests -c Debug
dotnet test tests/WeaveFleet.Application.Tests -c Debug
```
All commands should pass with zero errors.
