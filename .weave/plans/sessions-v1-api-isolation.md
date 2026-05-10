# Sessions V1 API Isolation

## TL;DR
> **Summary**: Fully isolate the V1 (Workspaces) session view from V2 (Projects) at every layer — schema, API, store, harness, WebSocket, and routing — so V1 can be frozen while V2 continues evolving.
> **Estimated Effort**: Large

## Context
### Original Request
Separate V1 and V2 session views at every level: database filtering, API endpoints, Pinia stores, harness managers, detail routes, and WebSocket channels. V1 gets frozen copies; V2 keeps evolving originals.

### Key Findings
- **Session entity** (`src/WeaveFleet.Domain/Entities/Session.cs`) has no `ViewMode` column today. 39 lines, straightforward to extend.
- **ISessionRepository** (`src/WeaveFleet.Domain/Repositories/ISessionRepository.cs`) — 47-line interface. `ListAsync`, `ListActiveAsync`, `CountAsync`, `GetStatusCountsAsync` all need `viewMode` filtering.
- **SessionRepository** (`src/WeaveFleet.Infrastructure/Data/Repositories/SessionRepository.cs`) — raw SQL with `StringBuilder`. Filtering by `view_mode` is a simple `AND view_mode = @ViewMode` clause.
- **SessionEndpoints** (`src/WeaveFleet.Api/Endpoints/SessionEndpoints.cs`) — single `MapGroup("/api/sessions")`. V1 needs a parallel `MapGroup("/api/sessions-v1")` with a reduced surface.
- **SessionOrchestrator** (`src/WeaveFleet.Application/Services/SessionOrchestrator.cs`) — 1281 lines. Creates sessions with `new Session { ... }`. Must set `ViewMode` on creation. The orchestrator is stateless over sessions; a V1-specific orchestrator isn't needed — just pass `viewMode` through `CreateSessionRequest`.
- **SessionService** (`src/WeaveFleet.Application/Services/SessionService.cs`) — thin wrapper around repo+orchestrator. Can be reused with a `viewMode` parameter on `ListSessionsAsync`.
- **Migrations** use DbUp with embedded `.sql` resources in `src/WeaveFleet.Infrastructure/Migrations/`. Latest is `019_*`. Next migration is `020_add_session_view_mode.sql`.
- **Client store** (`client/src/stores/sessions.ts`) — single Pinia store. V1 needs a parallel `sessions-v1.ts`.
- **useSessions composable** (`client/src/composables/use-sessions.ts`) — fetches from `/api/sessions`. V1 needs `useSessionsV1` fetching from `/api/sessions-v1`.
- **useSessionActions** (`client/src/composables/use-session-actions.ts`) — all mutations hit `/api/sessions/*`. V1 needs parallel actions hitting `/api/sessions-v1/*`.
- **SessionsV1Dashboard** (`client/src/components/sessions-v1/SessionsV1Dashboard.vue`) — currently uses the shared `useSessions` and `useSessionsStore`. Must be rewired to V1-specific store/composable.
- **Session detail route** (`client/src/routes/sessions.$id.tsx`) — fetches from `/api/sessions/{id}`. V1 needs `/sessions-v1/$id` fetching from `/api/sessions-v1/{id}`.
- **WebSocket** (`client/src/composables/use-weave-socket.ts`) — topic-based pub/sub. Sessions broadcast on topic `"sessions"`. V1 can use a separate topic `"sessions-v1"` — the backend `IEventBroadcaster` already supports arbitrary topic strings.
- **V1 view mode toggle** (`client/src/composables/use-sessions-view-mode.ts`) — already has `v1`/`v2`/`both` modes. Will remain as-is.
- **Session sync** (`client/src/lib/session-sync.ts`) — cross-component CustomEvent bus. V1 needs its own sync event key or the existing one can carry a `viewMode` discriminator.

## Objectives
### Core Objective
V1 and V2 sessions are fully isolated: separate API endpoints, stores, WebSocket topics, and detail routes. Changes to V2 cannot affect V1.

### Deliverables
- [ ] `view_mode` column on `sessions` table (migration `020`)
- [ ] V1-specific API endpoints at `/api/sessions-v1`
- [ ] V1-specific Pinia store and composables
- [ ] V1 detail route at `/sessions-v1/$id`
- [ ] V1 WebSocket topic `sessions-v1` for lifecycle events
- [ ] V1 `CreateSessionRequest` defaults `viewMode` to `"v1"`

### Definition of Done
- [ ] `dotnet build` succeeds with zero warnings in all projects
- [ ] `bun run type-check` passes in client
- [ ] V2 session list (`/api/sessions`) returns only `view_mode='v2'` sessions
- [ ] V1 session list (`/api/sessions-v1`) returns only `view_mode='v1'` sessions
- [ ] Creating a session from V1 UI creates it with `view_mode='v1'`
- [ ] V1 detail page loads from `/api/sessions-v1/{id}`
- [ ] WebSocket events for V1 sessions broadcast on `sessions-v1` topic
- [ ] Existing sessions remain accessible in V2 (no data migration needed — all default to `v2`)

### Guardrails (Must NOT)
- Must NOT break any existing V2 functionality
- Must NOT duplicate the `sessions` table — use `view_mode` column for filtering
- Must NOT create a separate `SessionOrchestrator` — reuse with `viewMode` parameter
- Must NOT introduce optional parameters in C# (strict coding standards)
- Must NOT change V2 API contract (`/api/sessions` response shape stays identical)

## TODOs

### Phase 1: Schema

- [x] 1. Add `view_mode` column to sessions table
  **What**: Create migration `020_add_session_view_mode.sql` that adds `view_mode TEXT NOT NULL DEFAULT 'v2'` to the `sessions` table. All existing sessions become `v2`.
  **Files**: `src/WeaveFleet.Infrastructure/Migrations/020_add_session_view_mode.sql`
  **Acceptance**: Migration applies cleanly. `SELECT DISTINCT view_mode FROM sessions` returns `'v2'`.

- [x] 2. Add `ViewMode` property to Session entity
  **What**: Add `public string ViewMode { get; set; } = "v2";` to `Session.cs`.
  **Files**: `src/WeaveFleet.Domain/Entities/Session.cs`
  **Acceptance**: Entity compiles. Default value is `"v2"`.

- [x] 3. Update SessionRepository to read/write `view_mode`
  **What**: Update `ReadSession` to map `view_mode`. Update `InsertAsync` to include `view_mode` in INSERT. Add `view_mode` filtering to `ListAsync`, `ListActiveAsync`, `CountAsync`, `GetStatusCountsAsync`. Add a `viewMode` parameter (non-optional) to the listing overloads used by the API layer. Keep existing overloads for backward compat internally (defaulting to no filter for system-level queries like `MarkAllNonTerminalStoppedAsync`).
  **Files**: `src/WeaveFleet.Infrastructure/Data/Repositories/SessionRepository.cs`
  **Acceptance**: Repository compiles. List queries filter by `view_mode` when parameter is provided.

- [x] 4. Update ISessionRepository interface
  **What**: Add new overloads for `ListAsync`, `ListActiveAsync`, `CountAsync`, `GetStatusCountsAsync` that accept a `string viewMode` parameter. Keep existing overloads for backward compatibility.
  **Files**: `src/WeaveFleet.Domain/Repositories/ISessionRepository.cs`
  **Acceptance**: Interface compiles. No breaking changes to existing callers.

### Phase 2: Backend V1 API

- [x] 5. Add `viewMode` to CreateSessionRequest
- [x] 6. Add `viewMode` parameter to SessionService.ListSessionsAsync
- [x] 7. Update existing `/api/sessions` endpoints to filter by `view_mode = 'v2'`
- [x] 8. Create `SessionV1Endpoints.cs` with `/api/sessions-v1` routes
- [x] 9. Register V1 endpoints in API startup
  **What**: Call `app.MapSessionV1Endpoints()` in the endpoint registration chain (likely in `Program.cs` or wherever `MapSessionEndpoints` is called).
  **Files**: Find and update the file that calls `MapSessionEndpoints()` (likely `src/WeaveFleet.Api/Program.cs` or an extension method).
  **Acceptance**: V1 endpoints are reachable at `/api/sessions-v1/*`.

### Phase 3: Backend V1 WebSocket Events

- [x] 10. Broadcast V1 session lifecycle events on `sessions-v1` topic
  **What**: In `SessionOrchestrator`, when creating/stopping/deleting/archiving a session, check `session.ViewMode` and broadcast on `"sessions-v1"` topic instead of `"sessions"` when view_mode is `"v1"`. The simplest approach: extract the topic selection into a helper `GetSessionsTopic(string viewMode) => viewMode == "v1" ? "sessions-v1" : "sessions"` and use it in all `BroadcastAsync` calls that currently hardcode `"sessions"`.
  **Files**: `src/WeaveFleet.Application/Services/SessionOrchestrator.cs`
  **Acceptance**: Creating a V1 session broadcasts `session_created` on topic `"sessions-v1"`. V2 sessions still broadcast on `"sessions"`.

### Phase 4: Client V1 Store

- [x] 11. Create V1 Pinia store
- [x] 12. Create `useSessionsV1` composable
- [x] 13. Create `useSessionActionsV1` composable
- [x] 14. Create V1 session sync utility
  **What**: Create `client/src/lib/session-sync-v1.ts` with `dispatchSessionV1Removed`, `dispatchSessionV1Upsert`, `addSessionV1SyncListener` — using event key `"weave:session-v1-sync"`. Alternatively, extend the existing `session-sync.ts` with a `viewMode` discriminator on the event detail. Prefer separate file for full isolation.
  **Files**: `client/src/lib/session-sync-v1.ts`
  **Acceptance**: V1 sync events are dispatched independently from V2.

### Phase 5: Client V1 Wiring

- [x] 15. Rewire SessionsV1Dashboard to V1 store/composables
  **What**: Update `SessionsV1Dashboard.vue` to import from `use-sessions-v1`, `useSessionsV1Store`, and `use-session-actions-v1`. The "New Session" button must call `useCreateSessionV1` (which sets `viewMode: "v1"` implicitly via the `/api/sessions-v1` endpoint). Update the session-select handler to navigate to `/sessions-v1/$id` instead of `/sessions/$id`.
  **Files**: `client/src/components/sessions-v1/SessionsV1Dashboard.vue`
  **Acceptance**: V1 dashboard fetches from V1 API, creates V1 sessions, and navigates to V1 detail route.

- [x] 16. Update V1 components to use V1 store
  **What**: Review `WorkspaceGroup.vue`, `WorkspaceSessionItem.vue`, `WorkspaceToolbar.vue` for any direct use of the shared sessions store or composables. Update to V1 equivalents where needed.
  **Files**: `client/src/components/sessions-v1/WorkspaceGroup.vue`, `client/src/components/sessions-v1/WorkspaceSessionItem.vue`, `client/src/components/sessions-v1/WorkspaceToolbar.vue`
  **Acceptance**: No V1 component imports from `use-sessions` or `useSessionsStore`.

- [x] 17. Subscribe V1 dashboard to `sessions-v1` WebSocket topic
  **What**: In the V1 dashboard (or a V1-specific composable), subscribe to WebSocket topic `"sessions-v1"` instead of `"sessions"` for real-time updates. Handle `session_created`, `session_stopped`, `session_deleted`, `session_archived`, `session_unarchived` events to update the V1 store.
  **Files**: `client/src/components/sessions-v1/SessionsV1Dashboard.vue` (or a new `client/src/composables/use-session-events-v1.ts`)
  **Acceptance**: V1 dashboard receives real-time updates only for V1 sessions.

### Phase 6: V1 Detail Route

- [x] 18. Create V1 session detail route
  **What**: Create `client/src/routes/sessions-v1.$id.tsx` — a frozen copy of `sessions.$id.tsx` that fetches from `/api/sessions-v1/{id}` instead of `/api/sessions/{id}`. Uses `useSessionsV1Store`. Navigation back goes to `/sessions-v1` instead of the main sessions view.
  **Files**: `client/src/routes/sessions-v1.$id.tsx`
  **Acceptance**: Navigating to `/sessions-v1/{id}` renders the detail view with data from the V1 API.

- [x] 19. Regenerate route tree
  **What**: Run TanStack Router codegen to pick up the new route file. This typically happens automatically on dev server start, but may need an explicit `bun run generate-routes` or equivalent.
  **Acceptance**: `client/src/routeTree.gen.ts` includes the new `sessions-v1/$id` route. No TypeScript errors.

### Phase 7: V1 Activity WebSocket

- [x] 20. Ensure V1 session activity status broadcasts on correct topic
  **What**: The `SessionActivityTracker` and `HarnessEventRelay` (or equivalent) broadcast activity status on `session:{sessionId}` topics — these are per-session and don't need view_mode changes. However, the `"activity"` topic used for initial snapshot on WS connect (in `WebSocketEndpoints.cs`) sends all sessions' activity. Consider whether V1 sessions should also appear in the `"activity"` snapshot (they should, for V1 dashboard live status). No change needed if the client filters by store membership.
  **Files**: (Investigation task — may require no changes)
  **Acceptance**: V1 sessions appear with correct activity status in V1 dashboard. V2 dashboard doesn't show V1 sessions.

## Verification

- [ ] `dotnet build` succeeds with zero warnings across all projects
- [ ] `bun run type-check` passes in client
- [ ] `GET /api/sessions` returns only V2 sessions (existing behavior preserved)
- [ ] `GET /api/sessions-v1` returns only V1 sessions (starts empty)
- [ ] `POST /api/sessions-v1` creates a session with `view_mode='v1'`
- [ ] `GET /api/sessions-v1/{id}` returns a V1 session
- [ ] V1 dashboard at `/sessions-v1` loads and creates sessions independently
- [ ] V1 detail page at `/sessions-v1/{id}` loads independently
- [ ] WebSocket topic `sessions-v1` receives V1 lifecycle events
- [ ] No regressions in V2 session flow (create, prompt, stop, resume, delete, archive)
- [ ] All existing tests pass
