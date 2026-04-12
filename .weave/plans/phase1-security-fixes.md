# Phase 1 — Security Fixes

## TL;DR
> **Summary**: Fix 4 blocking security vulnerabilities: unauthenticated GitHub plugin endpoints, global (non-user-scoped) token storage, missing instance ownership checks, and path traversal in skill endpoints.
> **Estimated Effort**: Medium

## Context

### Original Request
Address 4 BLOCKING security issues identified in an architecture review of the Weave Fleet .NET 10 solution. Each fix should be independently deployable as a vertical slice.

### Key Findings

**Issue 1 — Unauthenticated GitHub endpoints:**
- `EndpointExtensions.cs:61` calls `app.MapBackendPluginEndpoints()` directly on `app`, bypassing the `apiScope` authenticated route group built at lines 23-39.
- `GitHubBackendPlugin.MapEndpoints()` calls `GitHubEndpointMappings.MapAuthEndpoints(app)` and `MapDataEndpoints(app)`, both of which create route groups on the raw `WebApplication` — no authorization metadata.
- All `/api/integrations/github/*` endpoints (device flow, token storage, repo listing, issue proxying) are fully anonymous when auth is enabled.

**Issue 2 — Global token storage:**
- `IPluginStateStore` → `PluginStateStore` → `FileIntegrationStore` — all singleton. Storage path is `~/.weave/integrations.json`, a single JSON file with no user dimension.
- `GitHubService` (singleton) reads/writes tokens via `IPluginStateStore` using key `"github"`. Any authenticated user's token overwrites the previous user's token, and any user can read any stored token.
- The `Instance` entity already has a `UserId` column (migration 011), but plugin state has no equivalent.

**Issue 3 — Missing instance ownership:**
- `InstanceEndpoints.cs` uses `InstanceTracker.Get(id)` — a `ConcurrentDictionary<string, IHarnessInstance>` singleton with no user filtering.
- The DB `Instance` entity has a `UserId` field, and `InstanceService` already stores it. But the tracker and endpoints never verify the requesting user matches the instance owner.
- `InstanceService.GetInstanceAsync(id)` has no user filter either.

**Issue 4 — Path traversal in skills:**
- `SkillEndpoints.cs` uses `Path.Combine(SkillsDir, name)` on lines 38, 56, 81 with no validation.
- `Path.Combine` does NOT prevent `../` traversal — `Path.Combine("/skills", "../etc/passwd")` → `"/skills/../etc/passwd"` which resolves outside the skills dir.
- The `DELETE /{name}` endpoint calls `Directory.Delete(dir, recursive: true)` — a path traversal here is destructive.

## Objectives

### Core Objective
Eliminate all 4 identified security vulnerabilities with minimal, targeted changes that preserve existing API contracts and don't break E2E tests.

### Deliverables
- [x] GitHub plugin endpoints require authentication when auth is enabled
- [x] GitHub token storage is user-scoped (each user has their own token)
- [x] Instance endpoints verify the requesting user owns the instance
- [x] Skill endpoints validate path parameters to prevent directory traversal

### Definition of Done
- [x] `dotnet test` passes for all test projects (Api.Tests, Application.Tests, Infrastructure.Tests, Domain.Tests)
- [x] `dotnet build -c Release` succeeds with no warnings
- [x] Each fix has at least one targeted unit/integration test proving the vulnerability is closed
- [ ] Existing E2E auth tests remain green

### Guardrails (Must NOT)
- Must NOT change public API routes or response shapes (breaking frontend changes)
- Must NOT change the `IBackendPlugin` interface signature — plugin system extensibility is important
- Must NOT introduce a new database migration for Issue 2 (use file-based user scoping, consistent with current `FileIntegrationStore` approach)
- Must NOT over-engineer: no RBAC, no permission system, just ownership checks

## TODOs

---

### Issue 1: Authenticate GitHub Plugin Endpoints (CRITICAL)

- [x] 1. Move backend plugin endpoint mapping under the authenticated route group
  **What**: In `EndpointExtensions.MapFleetEndpoints()`, change line 61 from `app.MapBackendPluginEndpoints()` to `apiScope.MapBackendPluginEndpoints()`. This requires changing `MapBackendPluginEndpoints` to accept `IEndpointRouteBuilder` instead of `WebApplication`. Update `IBackendPlugin.MapEndpoints` — wait, guardrail says don't change the interface. Instead, change the `MapBackendPluginEndpoints` extension method to pass the `IEndpointRouteBuilder` scope and have each plugin map on that scope.

  **Approach**: The simplest fix that avoids changing `IBackendPlugin`:
  1. In `EndpointExtensions.cs`, change line 61 from `app.MapBackendPluginEndpoints()` to `apiScope.MapBackendPluginEndpoints()`.
  2. Change `MapBackendPluginEndpoints` to be an extension on `IEndpointRouteBuilder` instead of `WebApplication`. The method iterates plugins from DI and calls `plugin.MapEndpoints(app)` — but we need to pass the scoped builder. Since `IBackendPlugin.MapEndpoints` takes `WebApplication`, we can't change that interface.
  3. **Better approach**: Don't use `MapBackendPluginEndpoints` at all for endpoint registration. Instead, change `EndpointExtensions.MapFleetEndpoints` to call `app.MapBackendPluginEndpoints()` but have each plugin's endpoints explicitly require authorization. Do this by adding `.RequireAuthorization("FleetUser")` to the route groups created in `GitHubEndpointMappings.MapAuthEndpoints` and `MapDataEndpoints`.
  4. **Simplest approach**: Keep the call on `app` but add `RequireAuthorization("FleetUser")` to both route groups in `GitHubEndpointMappings.cs` (lines 14 and 93). This is the least invasive change and doesn't touch the plugin interface.

  **Final decision**: Apply `.RequireAuthorization("FleetUser")` to both `MapGroup(...)` calls in `GitHubEndpointMappings.cs`. This is plugin-local, requires no interface changes, and mirrors how `MapAuthenticatedGroup` works. Also inject `FleetOptions` to conditionally apply auth (matching the pattern from `EndpointExtensions.MapAuthenticatedGroup`).

  **Files**:
  - `src/WeaveFleet.Infrastructure/Plugins/BuiltIn/GitHub/GitHubEndpointMappings.cs` — add `.RequireAuthorization("FleetUser")` to both route groups, gated on `FleetOptions.Auth.Enabled`
  - `src/WeaveFleet.Infrastructure/Plugins/BuiltIn/GitHub/GitHubBackendPlugin.cs` — resolve and pass `FleetOptions` to mapping methods

  **Acceptance**:
  - Integration test: with auth enabled, unauthenticated GET to `/api/integrations/github/auth/status` returns 401
  - Integration test: with auth enabled and test auth, authenticated GET to `/api/integrations/github/auth/status` returns 200
  - Existing `GitHubPluginRegistrationTests` still passes

- [x] 2. Add integration tests for GitHub endpoint authentication
  **What**: Add a test class that boots the API with auth enabled + unauthorized handler and verifies GitHub endpoints return 401. Add a second test with auth enabled + test auth handler verifying 200/valid responses.

  **Files**:
  - `tests/WeaveFleet.Api.Tests/Endpoints/GitHubEndpointAuthTests.cs` — new test class

  **Acceptance**:
  - Tests assert 401 for unauthenticated requests to `/api/integrations/github/auth/status` and `/api/integrations/github/repos`
  - Tests pass with `dotnet test`

---

### Issue 2: User-Scoped GitHub Token Storage (CRITICAL)

- [x] 3. Make `IPluginStateStore` user-aware
  **What**: The `IPluginStateStore` and `IIntegrationStore` interfaces need a user dimension. Since `GitHubService` is singleton but needs per-user token access, the cleanest approach is:
  1. Add a `userId` parameter to `IPluginStateStore` methods (additive — won't break callers who don't use it yet).
  2. Actually, better: change `FileIntegrationStore` to scope storage by user. The store path becomes `~/.weave/integrations/{userId}.json` instead of `~/.weave/integrations.json`.
  3. But `FileIntegrationStore` is singleton and has no access to `IUserContext` (scoped).
  4. **Best approach**: Add `userId` parameter to `IIntegrationStore` and `IPluginStateStore` interfaces. Update `FileIntegrationStore` to use per-user file paths. Update `GitHubService` to accept `userId` in its methods and pass it through. Update `GitHubEndpointMappings` to resolve `IUserContext` and pass `userId` to `GitHubService`.

  **Files**:
  - `src/WeaveFleet.Application/Plugins/IPluginStateStore.cs` — add `userId` parameter to all methods
  - `src/WeaveFleet.Application/Services/IIntegrationStore.cs` — add `userId` parameter to all methods
  - `src/WeaveFleet.Infrastructure/Plugins/PluginStateStore.cs` — pass `userId` through to `IIntegrationStore`
  - `src/WeaveFleet.Infrastructure/Services/FileIntegrationStore.cs` — change storage path to `~/.weave/integrations/{userId}.json`, validate `userId` to prevent path traversal

  **Acceptance**:
  - Two users storing GitHub tokens get separate files on disk
  - `userId` parameter is validated (no empty, no path separators)

- [x] 4. Thread `userId` through `GitHubService` and endpoint mappings
  **What**: Update `GitHubService` methods to accept `userId` and pass it to `IPluginStateStore`. Update `GitHubEndpointMappings` to resolve `IUserContext` from DI and pass `userContext.UserId` to `GitHubService`.

  **Files**:
  - `src/WeaveFleet.Infrastructure/Services/GitHubService.cs` — add `userId` parameter to `GetTokenAsync`, `StoreTokenAsync`, `IsConnectedAsync`, `DisconnectAsync`, `PollForTokenAsync`, `ConnectWithTokenAsync`
  - `src/WeaveFleet.Infrastructure/Plugins/BuiltIn/GitHub/GitHubEndpointMappings.cs` — resolve `IUserContext` in each endpoint handler and pass `userContext.UserId` to `GitHubService`
  - `src/WeaveFleet.Infrastructure/Plugins/BuiltIn/GitHub/GitHubBackendPlugin.cs` — update `GetStatusAsync` to accept/use a userId (or use a system-level check)

  **Acceptance**:
  - User A's token is not accessible by User B
  - `GetTokenAsync("user-a")` returns null when only user-b has connected
  - Existing tests compile and pass after method signature updates

- [x] 5. Add unit tests for user-scoped integration store
  **What**: Test that `FileIntegrationStore` with userId creates per-user files and that cross-user access returns null.

  **Files**:
  - `tests/WeaveFleet.Infrastructure.Tests/Services/FileIntegrationStoreUserScopingTests.cs` — new test class

  **Acceptance**:
  - Test: Store config for user-a, retrieve for user-b → null
  - Test: Store config for user-a and user-b, each gets their own data
  - Test: userId with path separators is rejected

---

### Issue 3: Instance Endpoint Ownership Checks (HIGH)

- [x] 6. Add user ownership verification to `InstanceEndpoints`
  **What**: In each endpoint handler in `InstanceEndpoints.cs`, after retrieving the instance from `InstanceTracker`, verify that the requesting user owns the instance by checking the DB record's `UserId` against `IUserContext.UserId`. Use `InstanceService.GetInstanceAsync(id)` which already exists, then compare `instance.UserId != userContext.UserId` → return 404.

  **Approach**: Inject `IUserContext` and `InstanceService` into each endpoint handler. After the tracker lookup succeeds, do a DB ownership check. If the DB instance doesn't exist or the user doesn't match, return 404. This reuses the existing `Instance.UserId` column from migration 011.

  **Files**:
  - `src/WeaveFleet.Api/Endpoints/InstanceEndpoints.cs` — inject `IUserContext`, add ownership check after `tracker.Get(id)` in all 5 endpoint handlers (models, commands, command, agents, find/files)

  **Acceptance**:
  - User A cannot access User B's instance (returns 404, not 403 — avoid leaking instance existence)
  - Owner can still access their own instances normally

- [x] 7. Add integration tests for instance ownership isolation
  **What**: Create an integration test similar to `SessionEndpointTenantIsolationTests` that seeds instances for two users and verifies cross-user access is blocked.

  **Files**:
  - `tests/WeaveFleet.Api.Tests/Endpoints/InstanceEndpointTenantIsolationTests.cs` — new test class

  **Acceptance**:
  - Test: GET `/api/instances/{other-user-instance}/models` returns 404 for authenticated test-user
  - Test: GET `/api/instances/{own-instance}/models` returns 200 (or valid response) for owner

---

### Issue 4: Path Traversal Prevention in Skill Endpoints (HIGH)

- [x] 8. Add path validation helper and apply to `SkillEndpoints`
  **What**: Create a static helper method that canonicalizes a resolved path and verifies it is contained within the expected base directory. Apply this validation in all `SkillEndpoints` handlers that use the `name` parameter (GET `/{name}`, POST, DELETE `/{name}`).

  **Validation logic**:
  ```
  static bool IsContainedIn(string basePath, string candidatePath)
  {
      var fullBase = Path.GetFullPath(basePath) + Path.DirectorySeparatorChar;
      var fullCandidate = Path.GetFullPath(candidatePath);
      return fullCandidate.StartsWith(fullBase, StringComparison.OrdinalIgnoreCase);
  }
  ```

  Also reject `name` values that contain path separators (`/`, `\`) or are `.` / `..` as an early-out before even calling `Path.Combine`.

  **Files**:
  - `src/WeaveFleet.Api/Endpoints/SkillEndpoints.cs` — add path validation before every `Path.Combine(SkillsDir, name)` usage and before `Path.Combine(SkillsDir, req.Name)` usage. Return 400 for invalid names.

  **Acceptance**:
  - `GET /api/skills/../../../etc/passwd` returns 400 (not file contents)
  - `DELETE /api/skills/../../important-dir` returns 400 (no deletion)
  - `POST /api/skills` with `name: "../escape"` returns 400
  - Normal skill names (`my-skill`, `code_helper`) still work

- [x] 9. Add unit tests for skill path traversal prevention
  **What**: Test the path validation logic with malicious inputs including `../`, absolute paths, null bytes, and normal valid names.

  **Files**:
  - `tests/WeaveFleet.Api.Tests/Endpoints/SkillEndpointPathTraversalTests.cs` — new test class

  **Acceptance**:
  - Tests cover: `../../../etc/passwd`, `..`, `.`, `/absolute/path`, `name/with/slashes`, `valid-name`
  - All traversal attempts return 400
  - Valid names return expected responses (404 for non-existent, 200 for existing)

---

### Cross-cutting

- [x] 10. Verify all existing tests still pass
  **What**: Run the full test suite to ensure no regressions from any of the 4 fixes.

  **Acceptance**:
  - `dotnet test WeaveFleet.slnx` passes (all projects)
  - No new compiler warnings in Release build

## Verification

- [x] `dotnet build -c Release WeaveFleet.slnx` succeeds with zero warnings
- [x] `dotnet test WeaveFleet.slnx` — all test projects pass
- [x] E2E test: with auth enabled, unauthenticated API request to `/api/integrations/github/auth/status` returns 401 (`GitHubEndpointSecurityTests`)
- [x] E2E test: `GET /api/skills/../../../etc/passwd` returns 400 (`SkillPathTraversalSecurityTests`)
- [x] Code review confirms no new `Path.Combine` usages without validation in skill endpoints
- [x] Code review confirms all `InstanceEndpoints` handlers have ownership checks

## Implementation Order

The fixes are independent and can be developed in parallel, but a recommended order for sequential execution:

1. **Issue 4 (Path traversal)** — smallest change, highest risk-to-effort ratio, no interface changes
2. **Issue 1 (GitHub auth)** — small change, high impact, no interface changes
3. **Issue 3 (Instance ownership)** — moderate change, clear pattern from `SessionEndpointTenantIsolationTests`
4. **Issue 2 (User-scoped tokens)** — largest change, touches interfaces and multiple files

## Potential Pitfalls

| Risk | Impact | Mitigation |
|------|--------|------------|
| `IPluginStateStore` interface change breaks other callers | Compile errors | Search all usages — currently only `GitHubService`, `GitHubEndpointMappings`, `GitHubBackendPlugin`, and `PluginStateStore` use it. The interface is internal-facing. |
| `FileIntegrationStore` per-user files: existing `integrations.json` data lost | Existing single-user tokens disappear | Accept this — users re-authenticate once. Document in release notes. Alternatively, migrate existing file to `local-user` user path. |
| `GitHubBackendPlugin.GetStatusAsync` is called without user context (e.g., plugin listing) | Can't determine connected status per-user | Pass `IUserContext` through or accept that plugin status shows "disconnected" when no user context is available (system/background calls). |
| Instance ownership check adds a DB query per endpoint call | Performance | Acceptable — these are infrequent API calls, and `GetByIdAsync` is an indexed primary key lookup. |
| `SkillEndpoints` path validation on Windows vs Linux | `Path.DirectorySeparatorChar` differs | Use `StringComparison.OrdinalIgnoreCase` on Windows, `Ordinal` on Linux. `Path.GetFullPath` handles both. The `OrdinalIgnoreCase` is safe on Linux (more permissive) but correct behavior. |
