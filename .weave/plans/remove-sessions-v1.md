# Remove Sessions-V1 and Workspace Session Concept

## TL;DR
> **Summary**: Remove all sessions-v1 UI, API endpoints, ViewMode property, ManagedWorkspaceSessionSourceProvider, and the sessions view-mode setting. Workspace entity/service stays — it's used by sessions-v2 for worktree/clone isolation.
> **Estimated Effort**: Large

## Context
### Original Request
Remove the workspace-centric sessions-v1 view entirely. Sessions-v2 (project-grouped) is the only view going forward.

### Key Findings
- `WorkspaceId` is an FK on `Session` used by **both** v1 and v2 — workspaces provide worktree/clone isolation for v2 sessions too. **Do NOT remove Workspace entity, WorkspaceService, WorkspaceEndpoints, or workspaces table.**
- `ViewMode` property on `Session` (values `"v1"` / `"v2"`) controls topic routing (`"sessions-v1"` vs `"sessions"`) and query filtering. Removing it means always using `"sessions"` topic and dropping the `WHERE view_mode = @ViewMode` filter.
- `ManagedWorkspaceSessionSourceProvider` is v1-only (cloud auto-workspace). It should be removed along with its catalog entry and the legacy fallback in `SessionSourceResolutionService.TranslateLegacyRequest`.
- `OpenToolContextSubmenu.vue` lives in `components/sessions-v1/` but is imported by `components/sessions/SessionItem.vue` (v2). It must be **relocated**, not deleted.
- `SessionsViewSetting.vue` (settings page) and `use-sessions-view-mode.ts` composable control v1/v2 toggle — remove both.
- `IconRail.vue`, `ContextPanel.vue`, `AppShell.vue` all reference v1 — need cleanup.

## Objectives
### Core Objective
Eliminate all sessions-v1 code paths so only the project-grouped sessions-v2 view exists.

### Deliverables
- [ ] All v1 frontend routes, components, stores, composables, and sync removed
- [ ] V1 API endpoints removed
- [ ] `ViewMode` property removed from domain, services, repository
- [ ] `ManagedWorkspaceSessionSourceProvider` and catalog entry removed
- [ ] Database migration to drop `view_mode` column and index
- [ ] All tests updated

### Definition of Done
- [ ] `dotnet build` succeeds with no errors
- [ ] `dotnet test` passes all tests
- [ ] `npm run build` (client) succeeds
- [ ] No references to `sessions-v1`, `ViewMode`, `view_mode`, or `ManagedWorkspace` remain (except migration history)

### Guardrails (Must NOT)
- Do NOT remove `Workspace` entity, `WorkspaceService`, `WorkspaceEndpoints`, `IWorkspaceRepository`, or `workspaces` table
- Do NOT remove `WorkspaceId` from `Session` entity
- Do NOT break sessions-v2 functionality

## TODOs

- [x] 1. **Relocate OpenToolContextSubmenu**
  **What**: Move `client/src/components/sessions-v1/OpenToolContextSubmenu.vue` to `client/src/components/sessions/OpenToolContextSubmenu.vue`. Update the import in `client/src/components/sessions/SessionItem.vue`.
  **Files**: `client/src/components/sessions/OpenToolContextSubmenu.vue` (create), `client/src/components/sessions/SessionItem.vue` (update import path)
  **Acceptance**: `SessionItem.vue` imports from `@/components/sessions/OpenToolContextSubmenu.vue`; old file deleted in step 2.

- [x] 2. **Delete V1 frontend files**
  **What**: Delete all sessions-v1 frontend files:
    - `client/src/routes/sessions-v1.tsx`
    - `client/src/routes/sessions-v1_.$id.tsx`
    - `client/src/components/sessions-v1/` (entire directory — 7 files including the now-relocated `OpenToolContextSubmenu.vue`)
    - `client/src/stores/sessions-v1.ts`
    - `client/src/composables/use-sessions-v1.ts`
    - `client/src/composables/use-session-actions-v1.ts`
    - `client/src/composables/use-session-v1-events.ts`
    - `client/src/composables/use-sessions-view-mode.ts`
    - `client/src/lib/session-sync-v1.ts`
    - `client/src/components/settings/SessionsViewSetting.vue`
  **Files**: All files listed above (delete)
  **Acceptance**: None of these files exist on disk.

- [x] 3. **Clean up V1 references in layout components**
  **What**: Remove sessions-v1 references from:
    - `client/src/components/layout/IconRail.vue` — remove the `sessions-v1` nav item from the items array, remove the `isV1Enabled` filter, remove the `useSessionsViewMode` import, remove the `sessions-v1` case in `activeRailFromPath`
    - `client/src/components/layout/ContextPanel.vue` — remove `SessionsV1Panel` import and `"sessions-v1"` entry from panel map
    - `client/src/components/layout/AppShell.vue` — remove `SessionsV1RightPanel` import and the `sessions-v1` conditional
    - `client/src/stores/sidebar.ts` — remove `"sessions-v1"` from `SidebarRail` union type
    - `client/src/composables/use-session-detail-context.ts` — remove v1 references in comments/docs
  **Files**: `client/src/components/layout/IconRail.vue`, `client/src/components/layout/ContextPanel.vue`, `client/src/components/layout/AppShell.vue`, `client/src/stores/sidebar.ts`, `client/src/composables/use-session-detail-context.ts`
  **Acceptance**: No `sessions-v1` references in layout components. `npm run build` succeeds.

- [x] 4. **Regenerate route tree**
  **What**: Run TanStack Router code generation to regenerate `client/src/routeTree.gen.ts` without the deleted v1 route files.
  **Acceptance**: `routeTree.gen.ts` has no `sessions-v1` references.

- [x] 5. **Remove V1 API endpoints**
  **What**: Delete `src/WeaveFleet.Api/Endpoints/SessionV1Endpoints.cs`. Remove `MapSessionV1Endpoints()` call from `src/WeaveFleet.Api/Endpoints/EndpointExtensions.cs`. Remove the `SessionV1Endpoints` reference comment in `SessionEndpoints.cs` (line 483).
  **Files**: `src/WeaveFleet.Api/Endpoints/SessionV1Endpoints.cs` (delete), `src/WeaveFleet.Api/Endpoints/EndpointExtensions.cs`, `src/WeaveFleet.Api/Endpoints/SessionEndpoints.cs`
  **Acceptance**: `dotnet build` succeeds for `WeaveFleet.Api`.

- [x] 6. **Remove ViewMode from domain and Session entity**
  **What**: Remove `ViewMode` property from `src/WeaveFleet.Domain/Entities/Session.cs`. Remove `ViewMode` from `CreateSessionRequest` in `SessionOrchestrator.cs`.
  **Files**: `src/WeaveFleet.Domain/Entities/Session.cs`, `src/WeaveFleet.Application/Services/SessionOrchestrator.cs`
  **Acceptance**: No `ViewMode` property on `Session` or `CreateSessionRequest`.

- [x] 7. **Remove viewMode from repository layer**
  **What**: Remove `viewMode` parameter from all `ISessionRepository` methods (`ListAsync`, `CountAsync`, `GetStatusCountsAsync`, `ListActiveAsync`). Update `SessionRepository.cs` to remove `view_mode` from INSERT, SELECT WHERE clauses, and the `ReadSession` mapper. Update `InMemorySessionRepository` similarly.
  **Files**: `src/WeaveFleet.Domain/Repositories/ISessionRepository.cs`, `src/WeaveFleet.Infrastructure/Data/Repositories/SessionRepository.cs`, `tests/WeaveFleet.Testing/Fakes/Repositories/InMemorySessionRepository.cs`
  **Acceptance**: No `viewMode` or `view_mode` references in repository code.

- [x] 8. **Remove viewMode from services**
  **What**: Remove `viewMode` parameter from `SessionService.ListSessionsAsync`. Update `SessionOrchestrator` to: (a) remove `ViewMode = request.ViewMode` when creating sessions, (b) replace all `GetSessionsTopic(session.ViewMode)` calls with the literal `"sessions"`, (c) delete the `GetSessionsTopic` method, (d) remove `viewMode` parameter from `BroadcastStatusCountsAsync`.
  **Files**: `src/WeaveFleet.Application/Services/SessionService.cs`, `src/WeaveFleet.Application/Services/SessionOrchestrator.cs`
  **Acceptance**: No `viewMode`/`ViewMode` references in service layer.

- [x] 9. **Update SessionEndpoints to remove viewMode**
  **What**: The v2 `SessionEndpoints.cs` likely passes `viewMode: "v2"` to `SessionService.ListSessionsAsync` — update the call to match the new signature (no viewMode parameter).
  **Files**: `src/WeaveFleet.Api/Endpoints/SessionEndpoints.cs`
  **Acceptance**: `dotnet build` succeeds.

- [x] 10. **Remove ManagedWorkspaceSessionSourceProvider**
  **What**: Delete `src/WeaveFleet.Application/SessionSources/ManagedWorkspaceSessionSourceProvider.cs`. Remove its DI registration from `src/WeaveFleet.Infrastructure/DependencyInjection.cs` (line 90). Remove `ManagedWorkspaceStartSession` from `SessionSourceCatalog` in `SessionSourceContracts.cs` (the static property and its entry in `CoreDescriptors`). Remove `ManagedWorkspace` from `SessionSourceTypeNames`. Remove the managed-workspace fallback in `SessionSourceResolutionService.TranslateLegacyRequest` (lines 85-91). Remove `SessionSourceProviderIds.Managed`.
  **Files**: `src/WeaveFleet.Application/SessionSources/ManagedWorkspaceSessionSourceProvider.cs` (delete), `src/WeaveFleet.Infrastructure/DependencyInjection.cs`, `src/WeaveFleet.Application/SessionSources/SessionSourceContracts.cs`, `src/WeaveFleet.Application/Services/SessionSourceResolutionService.cs`
  **Acceptance**: No `ManagedWorkspace` references outside test files. `dotnet build` succeeds.

- [x] 11. **Update tests**
  **What**: 
    - `tests/WeaveFleet.Application.Tests/Services/ManagedWorkspaceTests.cs` — delete entire file
    - `tests/WeaveFleet.Application.Tests/Services/SessionOrchestratorTests.cs` — remove `CreateSessionAsync_InCloudModeWithoutDirectory_CreatesManagedWorkspaceSession` test, remove `ViewMode` assignments from session builders, update `ManagedWorkspaceSessionSourceProvider` removal from test builder
    - `tests/WeaveFleet.Application.Tests/Services/SessionSourceResolutionServiceTests.cs` — remove `ResolveCreateRequestAsync_InCloudModeWithoutDirectory_ReturnsManagedWorkspaceSelection` test
    - `tests/WeaveFleet.Testing/Builders/SessionOrchestratorBuilder.cs` — remove `ManagedWorkspaceSessionSourceProvider` from provider list
    - `tests/WeaveFleet.Testing/Fakes/Repositories/InMemorySessionRepository.cs` — already handled in step 7
    - `tests/WeaveFleet.Infrastructure.Tests/Data/Repositories/SessionRepositoryTests.cs` — remove any `ViewMode` assignments on test sessions
    - All other test files that set `ViewMode` on sessions — remove those assignments (default was `"v2"` which is being removed, so just delete the property set)
    - `tests/WeaveFleet.Api.Tests/Endpoints/SessionListEndpointAggregationTests.cs` — update if it references viewMode
  **Files**: `tests/WeaveFleet.Application.Tests/Services/ManagedWorkspaceTests.cs` (delete), `tests/WeaveFleet.Application.Tests/Services/SessionOrchestratorTests.cs`, `tests/WeaveFleet.Application.Tests/Services/SessionSourceResolutionServiceTests.cs`, `tests/WeaveFleet.Testing/Builders/SessionOrchestratorBuilder.cs`, `tests/WeaveFleet.Infrastructure.Tests/Data/Repositories/SessionRepositoryTests.cs`, `tests/WeaveFleet.Api.Tests/Endpoints/SessionListEndpointAggregationTests.cs`
  **Acceptance**: `dotnet test` passes.

- [x] 12. **Add database migration to drop view_mode column**
  **What**: Create a new migration `021_drop_session_view_mode.sql` (or next available number) that drops the `view_mode` column and its index from the `sessions` table.
  **Files**: `src/WeaveFleet.Infrastructure/Migrations/021_drop_session_view_mode.sql` (create)
  **Acceptance**: Migration applies cleanly. `SessionRepository` no longer reads `view_mode`.

- [x] 13. **Final verification sweep**
  **What**: Grep the entire codebase for any remaining references to `sessions-v1`, `ViewMode`, `view_mode`, `ManagedWorkspace`, `managed-workspace`, `SessionsV1`, `use-sessions-v1`, `session-sync-v1`. Only the old migration `020_add_session_view_mode.sql` and the new drop migration should reference `view_mode`.
  **Acceptance**: No stale references found outside migration SQL files.

## Verification
- [ ] `dotnet build` succeeds (all projects)
- [ ] `dotnet test` passes all tests
- [ ] `npm run build` (client) succeeds
- [ ] Grep verification confirms no stale v1 references
- [ ] Sessions-v2 functionality unaffected (workspace creation, worktree isolation still works)
