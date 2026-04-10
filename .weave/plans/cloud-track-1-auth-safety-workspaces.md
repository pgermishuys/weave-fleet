# Cloud Track 1: Auth + Cloud Safety + Managed Workspaces

## TL;DR
> **Summary**: Establish the shared foundation for all cloud functionality — authentication via Clerk/OIDC, `IUserContext` abstraction, `UserId`-scoped multi-tenancy across all entities, managed workspace enforcement in cloud mode, and cloud-mode endpoint restrictions with CSRF protections. This track must land first; Tracks 2 and 3 depend on it.
> **Estimated Effort**: Large

## Context

### Original Request
Split the approved `cloud-mvp-implementation.md` umbrella plan into three execution-ready tracks aligned with: (1) auth + cloud safety + managed workspaces, (2) hosted session runtime + provider credentials, (3) session sharing + collaborative PoC.

### Key Findings
- The codebase has clean architecture layers (Domain → Application → Infrastructure → API) with well-defined seams
- No `User` entity, `IUserContext`, auth middleware, or `UserId` on any entity exists today
- `FleetOptions` drives configuration; Constitution Principle #4 mandates same-binary/different-config
- 9 Dapper repositories need `UserId` filtering; 5 domain entities need `UserId` columns
- `FileIntegrationStore` handles plaintext credential storage — cloud mode needs encrypted, user-scoped replacement
- `WorkspaceService` supports `clone` and `worktree` strategies; cloud adds a `managed` strategy
- `SessionOrchestrator.CreateSessionAsync` is the critical integration point for cloud-mode guards

### Parent Plan
`cloud-mvp-implementation.md` — Architecture Decisions AD-1 through AD-10, AD-13 apply to this track.

### Relationship to Other Tracks
- **Track 2** (hosted session runtime + provider credentials) depends on `IUserContext`, `UserId`-scoped repositories, and managed workspace creation from this track
- **Track 3** (session sharing + collaborative PoC) depends on the multi-tenancy foundation and event-scoping seams from this track
- **Infrastructure workstream A** (Lightsail provisioning, Caddy, systemd) can run in parallel with this track

## Objectives

### Core Objective
An authenticated user can sign in via Clerk, see only their own data, and create sessions that use managed workspaces in cloud mode — with all cloud-unsafe endpoints restricted and CSRF protections in place.

### Deliverables
- [ ] `IUserContext` interface with `LocalUserContext`, `ClaimsUserContext`, and `SystemUserContext` implementations
- [ ] `User` entity + repository + shadow record creation on first login
- [ ] `AuthOptions` and `CloudOptions` in `FleetOptions`
- [ ] ASP.NET Core cookie + OIDC authentication pipeline (Clerk primary)
- [ ] Auth requirements on all endpoint groups with explicit public/auth/disabled classification
- [ ] CSRF / antiforgery protections for state-changing endpoints
- [ ] Auth-aware CORS
- [ ] `UserId` columns on all 5 domain entities with backwards-compatible migration
- [ ] `IUserContext`-scoped queries in all 9 repositories
- [ ] Managed workspace creation under `{WorkspaceRoot}/{userStorageKey}/{workspaceId}`
- [ ] Rejection of arbitrary `Directory` input in cloud mode
- [ ] User-scoped WebSocket and SSE event delivery (owner-only scope; participant fan-out seam prepared for Track 3)
- [ ] Ownership guards on delegation/callback paths
- [ ] Frontend auth provider (cookie-based, no bearer tokens in browser)
- [ ] `GET /api/user/me` endpoint
- [ ] `GET /api/config/client` cloud-mode config endpoint
- [ ] `appsettings.Cloud.json`

### Non-Goals
- Provider credential storage and injection (Track 2)
- Session sharing model, access service, and sharing endpoints (Track 3)
- Onboarding wizard and first-run flow (Track 2)
- Infrastructure provisioning — Lightsail, Caddy, systemd, deploy scripts (parallel infra workstream)
- Data Protection key persistence configuration (Track 2, co-delivered with credential encryption)

### Definition of Done
- [ ] `dotnet build` succeeds with zero errors
- [ ] `dotnet test` passes — all existing and new tests
- [ ] Local mode (`Auth.Enabled=false`, `Cloud.Enabled=false`) works identically to today
- [ ] Cloud mode: unauthenticated product API/SSE/WebSocket requests → `401`/`403`
- [ ] Cloud mode: `CreateSessionRequest.Directory` is rejected → 400
- [ ] Cloud mode: User A cannot see User B's sessions/projects/workspaces
- [ ] Cloud mode: managed workspace created under `{WorkspaceRoot}/{userStorageKey}/{workspaceId}`
- [ ] Cloud mode: no bearer tokens in request URLs or logs
- [ ] Cloud mode: public endpoint allowlist enforced (`/healthz`, `/readyz` only)
- [ ] Cloud mode: CSRF protection active on state-changing endpoints

### Guardrails (Must NOT)
- Must NOT allow arbitrary server path input in cloud mode
- Must NOT break local mode — all changes backwards-compatible
- Must NOT introduce containers or PostgreSQL
- Must NOT place bearer tokens in URLs, including WebSocket query strings
- Must NOT use raw external identity values directly in filesystem paths
- Must NOT add organization/team features
- Must NOT implement provider credential storage (that's Track 2)
- Must NOT implement session sharing (that's Track 3)

## TODOs

### Phase 1: Identity Foundation — "A user can sign in and see an empty dashboard"

- [x] 1. Create IUserContext interface
  **What**: Define `IUserContext` in the Application layer. Properties: `UserId` (string), `Email` (string?), `DisplayName` (string?), `IsAuthenticated` (bool). Single seam for all downstream code.
  **Files**: `src/WeaveFleet.Application/Services/IUserContext.cs`
  **Acceptance**: Interface compiles. Can be injected by any scoped service.

- [x] 2. Create LocalUserContext implementation
  **What**: Returns constant values: `UserId = "local-user"`, `Email = null`, `DisplayName = "Local User"`, `IsAuthenticated = true`. Registered when `Auth.Enabled = false`.
  **Files**: `src/WeaveFleet.Infrastructure/Services/LocalUserContext.cs`
  **Acceptance**: Returns hardcoded values. No external dependencies.

- [x] 3. Create ClaimsUserContext implementation
  **What**: Extracts identity from `HttpContext.User.Claims` via `IHttpContextAccessor`. Maps OIDC claims: `sub` → `UserId`, `email` → `Email`, `name` → `DisplayName`. Registered when `Auth.Enabled = true`.
  **Files**: `src/WeaveFleet.Infrastructure/Services/ClaimsUserContext.cs`
  **Acceptance**: Correctly extracts claims. Throws descriptive error if `sub` claim missing.

- [x] 4. Create SystemUserContext for background services
  **What**: Implementation for hosted services and startup recovery that run without `HttpContext`. `UserId = "system"`, `IsAuthenticated = false`. Used by `HarnessEventRelay`, analytics, and `MarkAllStoppedAsync`.
  **Files**: `src/WeaveFleet.Infrastructure/Services/SystemUserContext.cs`
  **Acceptance**: Background services function. Startup recovery works.

- [x] 5. Create User entity
  **What**: Domain entity for shadow user records. Fields: `Id` (string, matches IdP `sub`), `Email` (string), `DisplayName` (string?), `Status` (string, default "active"), `CreatedAt` (string), `LastLoginAt` (string?), `OnboardingCompletedAt` (string?).
  **Files**: `src/WeaveFleet.Domain/Entities/User.cs`
  **Acceptance**: Sealed class following `Session.cs` pattern.

- [x] 6. Create IUserRepository and DapperUserRepository
  **What**: Interface with `GetByIdAsync`, `UpsertAsync`, `UpdateLastLoginAsync`, `UpdateOnboardingCompletedAsync`. Dapper implementation following `DapperProjectRepository` pattern.
  **Files**: `src/WeaveFleet.Domain/Repositories/IUserRepository.cs`, `src/WeaveFleet.Infrastructure/Data/Repositories/DapperUserRepository.cs`
  **Acceptance**: All methods implemented. CRUD works against SQLite.

- [x] 7. Create users table migration
  **What**: SQL migration creating `users` table with all entity columns. Index on `email`.
  **Files**: `src/WeaveFleet.Infrastructure/Migrations/010_add_users_table.sql`
  **Acceptance**: Migration runs cleanly on fresh and existing databases.

- [x] 8. Add AuthOptions and CloudOptions to FleetOptions
  **What**: `AuthOptions`: `Enabled`, `Authority`, `ClientId`, `ClientSecret`, `CallbackPath`, `SignedOutCallbackPath`, `AllowedOrigins`, cookie settings. `CloudOptions`: `Enabled`, `WorkspaceRoot`. Add `Auth` and `Cloud` properties to `FleetOptions`.
  **Files**: `src/WeaveFleet.Application/Configuration/FleetOptions.cs`
  **Acceptance**: `Fleet:Auth:Enabled` controls auth. `Fleet:Cloud:Enabled` controls cloud mode. `Fleet:Cloud:WorkspaceRoot` sets root.

- [x] 9. Add ASP.NET Core cookie + OIDC authentication to Program.cs
  **What**: When `Auth.Enabled = true`, configure cookie auth (default scheme) + OIDC (challenge scheme). Configure authority, client ID, callback paths, claim mapping, secure cookie settings. Add `UseAuthentication()` + `UseAuthorization()`. Register conditional `IUserContext`. Register `IHttpContextAccessor`.
  **Files**: `src/WeaveFleet.Api/Program.cs`
  **Acceptance**: Cookie/OIDC active when auth enabled. `IUserContext` always resolvable. Unauthenticated requests challenge correctly.

- [x] 10. Add auth requirements to all endpoint groups
  **What**: `.RequireAuthorization()` on all `MapGroup()` calls when auth enabled. "FleetUser" policy: enforced in cloud, allows anonymous in local. Exclude `/healthz`, `/readyz`. Cloud-mode endpoint audit for local-only endpoints. WebSocket auth via cookie/session + origin validation.
  **Files**: All files in `src/WeaveFleet.Api/Endpoints/` (modify `MapGroup` calls), `src/WeaveFleet.Api/Endpoints/WebSocketEndpoints.cs`
  **Acceptance**: Product endpoints return `401`/`403` (not IdP redirects). Health checks public. Local mode unchanged. No bearer tokens in URLs.

- [x] 11. Create UserService with first-login sync
  **What**: `EnsureUserAsync(IUserContext ctx)` — creates shadow `User` record on first login, updates `LastLoginAt`. Called from endpoint filter or middleware.
  **Files**: `src/WeaveFleet.Application/Services/UserService.cs`
  **Acceptance**: First request from new user creates DB record. Subsequent requests update last login.

- [x] 12. Add user status endpoint
  **What**: `GET /api/user/me` returning `userId`, `email`, `displayName`, `onboardingCompleted`, `createdAt`.
  **Files**: `src/WeaveFleet.Api/Endpoints/UserEndpoints.cs`, `src/WeaveFleet.Api/Endpoints/EndpointExtensions.cs`
  **Acceptance**: Returns 200 for authenticated users. 401 for unauthenticated.

- [x] 13. Register identity services in DI
  **What**: Register `IUserRepository`/`DapperUserRepository`, `UserService`, conditional `IUserContext`, `IHttpContextAccessor`, `SystemUserContext` for hosted service scopes.
  **Files**: `src/WeaveFleet.Infrastructure/DependencyInjection.cs`
  **Acceptance**: All services resolvable. `dotnet build` succeeds.

- [x] 14. Add auth-aware CORS
  **What**: When auth enabled, restrict CORS origins to `Fleet:Auth:AllowedOrigins`. Add `AllowCredentials()`.
  **Files**: `src/WeaveFleet.Api/Program.cs`
  **Acceptance**: CORS allows configured origins only in cloud mode. Dev mode unchanged.

- [x] 15. Add antiforgery / CSRF protections
  **What**: ASP.NET Core antiforgery for all state-changing endpoints. Define exemptions explicitly. Frontend sends antiforgery token/header.
  **Files**: `src/WeaveFleet.Api/Program.cs`, relevant endpoints, frontend API client/auth utilities
  **Acceptance**: State-changing requests without valid antiforgery → rejected. Valid requests succeed.

- [x] 16. Add frontend auth provider
  **What**: Frontend auth/session layer for cookie-based auth. Detect authenticated vs unauthenticated, redirect to login, stop attaching bearer tokens. Clerk UI integration for redirect/login UX only.
  **Files**: `client/src/contexts/auth-context.tsx`, `client/src/app.tsx`, `client/src/lib/api-client.ts`, `client/package.json`
  **Acceptance**: Auth enabled → unauthenticated users redirect to login. API calls succeed via cookie.

- [x] 17. Create cloud appsettings
  **What**: `appsettings.Cloud.json` with `Auth.Enabled = true`, `Host = "0.0.0.0"`, `Port = 8080`, `Cloud.Enabled = true`, `Cloud.WorkspaceRoot = "/data/workspaces"`, `DatabasePath`, OIDC placeholders. Sensitive values via env vars.
  **Files**: `src/WeaveFleet.Api/appsettings.Cloud.json`
  **Acceptance**: Application starts with `ASPNETCORE_ENVIRONMENT=Cloud` and picks up config.

- [x] 18. Add cloud mode config endpoint
  **What**: `GET /api/config/client` returning `{ cloudMode, authEnabled, availableHarnesses }`. Frontend uses this to adapt UI.
  **Files**: `src/WeaveFleet.Api/Endpoints/ConfigEndpoints.cs`
  **Acceptance**: Returns correct cloud mode status. Auth/public classification explicit and tested.

- [x] 19. Write identity and CSRF tests
  **What**: Unit tests for `LocalUserContext`, `ClaimsUserContext`. Integration tests for authenticated → 200, unauthenticated → 401. CSRF rejection tests.
  **Files**: `tests/WeaveFleet.Infrastructure.Tests/Services/LocalUserContextTests.cs`, `tests/WeaveFleet.Infrastructure.Tests/Services/ClaimsUserContextTests.cs`
  **Acceptance**: Tests pass. Both auth modes covered.

### Phase 2: Multi-Tenancy + Managed Workspaces — "Users see only their own data"

- [x] 20. Add UserId to all domain entities
  **What**: Add `public string UserId { get; set; } = string.Empty;` to: `Project`, `Session`, `Workspace`, `Instance`, `WorkspaceRoot`.
  **Files**: `src/WeaveFleet.Domain/Entities/Project.cs`, `src/WeaveFleet.Domain/Entities/Session.cs`, `src/WeaveFleet.Domain/Entities/Workspace.cs`, `src/WeaveFleet.Domain/Entities/Instance.cs`, `src/WeaveFleet.Domain/Entities/WorkspaceRoot.cs`
  **Acceptance**: All 5 entities have `UserId` property.

- [x] 21. Create UserId migration
  **What**: `ALTER TABLE` for all 5 tables adding `user_id TEXT NOT NULL DEFAULT 'local-user'`. Indexes on `user_id`. Compound indexes: `(user_id, status)` on sessions, `(user_id, type)` on projects.
  **Files**: `src/WeaveFleet.Infrastructure/Migrations/011_add_user_id_columns.sql`
  **Acceptance**: Migration runs cleanly. Existing data gets `user_id = 'local-user'`.

- [x] 22. Inject IUserContext into all repositories
  **What**: Modify all 9 Dapper repositories. Add `WHERE user_id = @UserId` to owner-scoped `SELECT` queries. Add `user_id` to `INSERT` statements. Prepare access-controlled lookup seams for Track 3 sharing. System/recovery operations bypass user filter.
  **Files**: `src/WeaveFleet.Infrastructure/Data/Repositories/DapperDelegationRepository.cs`, `src/WeaveFleet.Infrastructure/Data/Repositories/DapperInstanceRepository.cs`, `src/WeaveFleet.Infrastructure/Data/Repositories/DapperMessageRepository.cs`, `src/WeaveFleet.Infrastructure/Data/Repositories/DapperProjectRepository.cs`, `src/WeaveFleet.Infrastructure/Data/Repositories/DapperSessionCallbackRepository.cs`, `src/WeaveFleet.Infrastructure/Data/Repositories/DapperSessionRepository.cs`, `src/WeaveFleet.Infrastructure/Data/Repositories/DapperSessionSourceUsageRepository.cs`, `src/WeaveFleet.Infrastructure/Data/Repositories/DapperWorkspaceRepository.cs`, `src/WeaveFleet.Infrastructure/Data/Repositories/DapperWorkspaceRootRepository.cs`
  **Acceptance**: User A's queries never return User B's data.

- [x] 23. Update services to set UserId on entities
  **What**: Inject `IUserContext` into `SessionOrchestrator`, `ProjectService`, `WorkspaceService`, `InstanceService`, `SessionService`. Set `UserId` on entity creation. `EnsureScratchProjectAsync` creates per-user scratch project.
  **Files**: `src/WeaveFleet.Application/Services/SessionOrchestrator.cs`, `src/WeaveFleet.Application/Services/ProjectService.cs`, `src/WeaveFleet.Application/Services/WorkspaceService.cs`, `src/WeaveFleet.Application/Services/InstanceService.cs`, `src/WeaveFleet.Application/Services/SessionService.cs`
  **Acceptance**: All newly created entities have correct `UserId`.

- [x] 24. Implement managed workspace creation
  **What**: Modify `WorkspaceService.CreateWorkspaceAsync` — when `Cloud.Enabled`: derive path-safe `UserStorageKey`, generate `{WorkspaceRoot}/{userStorageKey}/{workspaceId}`, canonicalize, verify under root, create directory. `IsolationStrategy = "managed"`.
  **Files**: `src/WeaveFleet.Application/Services/WorkspaceService.cs`, `src/WeaveFleet.Application/Configuration/FleetOptions.cs`
  **Acceptance**: Cloud mode: managed workspace under configured root with path-safe key. Path verified under root. Local mode: unchanged.

- [x] 25. Block arbitrary Directory input in cloud mode
  **What**: In `SessionOrchestrator.CreateSessionAsync`, when `Cloud.Enabled = true`, reject requests with `Directory` value → `FleetError.ValidationError`. Apply to fork paths too.
  **Files**: `src/WeaveFleet.Application/Services/SessionOrchestrator.cs`
  **Acceptance**: `POST /api/sessions` with `Directory` in cloud mode → 400. Without → managed workspace created.

- [x] 26. Scope WebSocket events by user (owner-only)
  **What**: Modify `InMemoryEventBroadcaster` for user-scoped delivery. WebSocket endpoint authenticates via cookie/session + origin validation. Prepare the `IEventBroadcaster` interface for session-participant fan-out (seam for Track 3) but implement owner-only scoping now.
  **Files**: `src/WeaveFleet.Application/Services/IEventBroadcaster.cs`, `src/WeaveFleet.Infrastructure/Services/InMemoryEventBroadcaster.cs`, `src/WeaveFleet.Api/Endpoints/WebSocketEndpoints.cs`, `src/WeaveFleet.Application/Services/SessionOrchestrator.cs`, `src/WeaveFleet.Infrastructure/Services/HarnessEventRelay.cs`
  **Acceptance**: Events delivered only to the session owner. Seam exists for Track 3 to extend to participants.

- [x] 27. Add ownership guards to delegation/callback paths
  **What**: Verify cross-session references belong to the same user: `OnComplete.NotifySessionId`, ancestor chain walks, parent references, `HarnessEventRelay.TryFireCallbacksAsync`.
  **Files**: `src/WeaveFleet.Application/Services/SessionOrchestrator.cs`, `src/WeaveFleet.Infrastructure/Services/HarnessEventRelay.cs`
  **Acceptance**: Cross-user session references → `FleetError.Unauthorized`.

- [x] 28. Scope SSE activity stream by user
  **What**: Modify `SessionEventEndpoints` to filter events by user ownership. Prepare seam for Track 3 shared-session access.
  **Files**: `src/WeaveFleet.Api/Endpoints/SessionEventEndpoints.cs`
  **Acceptance**: SSE stream delivers only events for sessions the authenticated user owns.

- [x] 29. Write multi-tenancy and managed workspace tests
  **What**: Tests: User A creates session → User B can't see it. WebSocket isolation. Cross-user delegation rejection. System recovery works. Managed workspace under correct path. Hostile identity/path tests for path-safe storage key and canonical path enforcement.
  **Files**: `tests/WeaveFleet.Application.Tests/Services/MultiTenancyTests.cs`, `tests/WeaveFleet.Application.Tests/Services/ManagedWorkspaceTests.cs`
  **Acceptance**: All tests pass. Zero cross-tenant leakage.

## Dependencies

### This track depends on
- Nothing — this is the foundation track

### Tracks that depend on this
- **Track 2** depends on: `IUserContext` (TODO 1-4), `UserId`-scoped repositories (TODO 22), managed workspace creation (TODO 24), `FleetOptions.Cloud` (TODO 8), `appsettings.Cloud.json` (TODO 17)
- **Track 3** depends on: all of the above, plus event-scoping seams (TODO 26, 28), ownership guards (TODO 27)

### Infrastructure parallelism
The following infrastructure tasks from the umbrella plan (Workstream A) can proceed **in parallel** with this track:
- A.1 Provision Lightsail instance
- A.2 Install runtime dependencies
- A.3 Configure TLS with Caddy
- A.4 Configure Clerk application (needed by TODO 9 for OIDC config values, but Clerk setup itself is independent)
- A.5 Create deployment script
- A.6 Create systemd service
- A.7 Add hosted runtime confinement setup

## Verification

- [ ] `dotnet build` succeeds with zero errors
- [ ] `dotnet test` — all existing tests pass (no regressions)
- [ ] `dotnet test` — all new tests pass
- [ ] Local mode: application works identically to before
- [ ] Cloud mode: unauthenticated product API/SSE/WebSocket → `401`/`403`
- [ ] Cloud mode: `CreateSessionRequest.Directory` rejected → 400
- [ ] Cloud mode: User A cannot see User B's data
- [ ] Cloud mode: managed workspace created under correct root
- [ ] Cloud mode: public endpoint allowlist enforced
- [ ] Cloud mode: CSRF active on state-changing endpoints
- [ ] Cloud mode: no bearer tokens in URLs or logs

## Risks

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Existing test suite breaks from UserId changes | High | Medium | Migration uses `DEFAULT 'local-user'`. Test helpers default `UserId = "test-user"`. Run full suite after each phase. |
| Cookie/OIDC integration complexity | Medium | Medium | Use standard ASP.NET Core cookie + OIDC. Keep browser token-free. |
| 9 repository modifications risk regressions | Medium | Medium | Modify incrementally, run tests after each batch. |

## Follow-up Notes

- Warp follow-up: backend plugin endpoints still need an auth review. In particular, GitHub plugin backend routes under `/api/integrations/github/**` appear to be mapped outside the main authenticated API grouping and should be explicitly protected before cloud rollout.
- Warp follow-up: GitHub/plugin integration state is still effectively global rather than per-user. Current storage/service flow should be isolated by `UserId` before cloud use so one user cannot connect/disconnect or reuse another user's GitHub integration state.
- Verification environment note: full `dotnet test -c Release` is currently environment-sensitive in this worktree because E2E attempts a frontend build (`bun run build` / `vite`) and some Windows symlink tests require symlink privileges. Keep these blockers explicit when evaluating CI/manual verification status.
