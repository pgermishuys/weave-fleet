# Centralized Plugin Auth

## TL;DR
> **Summary**: Change `IBackendPlugin.MapEndpoints` to receive an `IEndpointRouteBuilder` (pre-authenticated when auth is enabled) instead of `WebApplication`, making plugin auth centralized and secure by default. Remove manual auth logic from the GitHub plugin.
> **Estimated Effort**: Short

## Context
### Original Request
The backend plugin system has a security-by-design flaw: `IBackendPlugin.MapEndpoints(WebApplication app)` gives plugins the raw `WebApplication`, so they map endpoints **outside** the authenticated route group. The only production plugin (GitHub) was manually patched to resolve `FleetOptions` and call `RequireAuthorization("FleetUser")` itself — a fragile pattern that new plugins would silently miss, creating unauthenticated endpoints.

### Key Findings
1. **`EndpointExtensions.MapFleetEndpoints()`** (line 61) calls `app.MapBackendPluginEndpoints()` on the raw `app`, while all core endpoints use the `apiScope` (which is an authenticated `RouteGroupBuilder` when auth is enabled).
2. **`IBackendPlugin.MapEndpoints(WebApplication app)`** — the interface takes `WebApplication`, forcing plugins to handle auth themselves.
3. **`GitHubBackendPlugin.MapEndpoints`** — resolves `FleetOptions` from the service provider and passes it to `GitHubEndpointMappings.MapAuthEndpoints` and `MapDataEndpoints`, which each create their own route groups and conditionally call `RequireAuthorization("FleetUser")`.
4. **`GitHubEndpointMappings`** — both `MapAuthEndpoints` and `MapDataEndpoints` accept `WebApplication` + `FleetOptions`, create `MapGroup(...)`, then conditionally apply `.RequireAuthorization("FleetUser")`. This is the duplicated auth logic to remove.
5. **`MapBackendPluginEndpoints`** is called in two places: `EndpointExtensions.cs` line 61 (production) and `GitHubPluginRegistrationTests.cs` line 19 (test).
6. **`StubBackendPlugin`** in `GitHubPluginRegistrationTests` implements `MapEndpoints(WebApplication app)` — must be updated.
7. **`BuiltInPluginCatalog`** uses `IBackendPlugin` but only accesses `Descriptor` and `GetStatusAsync` — unaffected by `MapEndpoints` signature change.
8. The `MapAuthenticatedGroup` helper in `EndpointExtensions` exists but is unused after this refactor — it can be removed or left (not in scope).

## Objectives
### Core Objective
Make plugin endpoint auth centralized and secure by default — plugins receive a pre-authenticated `IEndpointRouteBuilder` and never need to know about auth configuration.

### Deliverables
- [ ] `IBackendPlugin.MapEndpoints` takes `IEndpointRouteBuilder` instead of `WebApplication`
- [ ] `MapBackendPluginEndpoints` passes the authenticated `apiScope` to plugins
- [ ] GitHub plugin cleaned up: no manual `FleetOptions` resolution or `RequireAuthorization` calls
- [ ] All tests updated and passing

### Definition of Done
- [ ] `dotnet build src/WeaveFleet.Api` succeeds
- [ ] `dotnet build tests/WeaveFleet.Api.Tests` succeeds
- [ ] `dotnet test tests/WeaveFleet.Api.Tests` — all tests pass (including `GitHubEndpointAuthTests` and `GitHubPluginRegistrationTests`)
- [ ] `dotnet test tests/WeaveFleet.E2E` — E2E auth tests pass (if runnable in local env)
- [ ] `grep -r "RequireAuthorization" src/WeaveFleet.Infrastructure/Plugins/` returns zero matches
- [ ] `grep -r "FleetOptions" src/WeaveFleet.Infrastructure/Plugins/BuiltIn/GitHub/GitHubEndpointMappings.cs` returns zero matches

### Guardrails (Must NOT)
- Must NOT change public API routes (all `/api/integrations/github/...` paths stay the same)
- Must NOT change response shapes or status codes
- Must NOT introduce a plugin permission system or over-engineer
- Must NOT break the GitHub plugin's actual behavior (auth when enabled, no-auth when disabled)
- Must NOT change `FleetOptions`, auth policies, or the OIDC configuration
- Must NOT modify `BuiltInPluginCatalog` — it only uses `Descriptor`/`GetStatusAsync`

## TODOs

- [x] 1. Change `IBackendPlugin.MapEndpoints` signature
  **What**: Change the parameter from `WebApplication app` to `IEndpointRouteBuilder builder`. Update the `using` directive from `Microsoft.AspNetCore.Builder` to `Microsoft.AspNetCore.Routing` (since `IEndpointRouteBuilder` lives in `Microsoft.AspNetCore.Routing`). Keep `Microsoft.AspNetCore.Builder` too if needed for other usages, but `IEndpointRouteBuilder` requires `Microsoft.AspNetCore.Routing`.
  **Files**: `src/WeaveFleet.Application/Plugins/IBackendPlugin.cs`
  **Acceptance**: Interface compiles with the new signature. The `using` directives include `Microsoft.AspNetCore.Routing`.

- [x] 2. Move plugin endpoint mapping into the authenticated scope
  **What**: In `EndpointExtensions.cs`, make two changes:
  (a) Move `app.MapBackendPluginEndpoints()` (line 61) up into the `apiScope` block, changing it to `apiScope.MapBackendPluginEndpoints()` — so it sits alongside the other `apiScope.Map*()` calls (e.g., after line 58, before the `return`).
  (b) Change the `MapBackendPluginEndpoints` extension method signature from `this WebApplication app` to `this IEndpointRouteBuilder builder`. Update the method body to pass `builder` (the pre-authenticated scope) to each plugin's `MapEndpoints`. Change the return type from `WebApplication` to `IEndpointRouteBuilder`. Update the `GetServices<IBackendPlugin>()` call to use the service provider — since `IEndpointRouteBuilder` exposes `ServiceProvider`, use `builder.ServiceProvider.GetServices<IBackendPlugin>()`.
  **Files**: `src/WeaveFleet.Api/Endpoints/EndpointExtensions.cs`
  **Acceptance**: `MapBackendPluginEndpoints` accepts `IEndpointRouteBuilder`, resolves plugins from `builder.ServiceProvider`, and passes the builder to each plugin. Plugin endpoints are now mapped inside `apiScope` and inherit its auth policy automatically.

- [x] 3. Clean up `GitHubEndpointMappings` — remove manual auth
  **What**: Change both `MapAuthEndpoints` and `MapDataEndpoints` to accept `IEndpointRouteBuilder builder` instead of `WebApplication app, FleetOptions fleetOptions`. Remove the `FleetOptions` parameter entirely. Remove the `if (fleetOptions.Auth.Enabled) group.RequireAuthorization("FleetUser")` blocks from both methods. Change `app.MapGroup(...)` to `builder.MapGroup(...)`. Remove the `using WeaveFleet.Application.Configuration;` import (no longer needed since `FleetOptions` is no longer referenced). Keep all endpoint definitions, route paths, handler logic, and `WithName`/`WithTags` calls exactly as-is.
  **Files**: `src/WeaveFleet.Infrastructure/Plugins/BuiltIn/GitHub/GitHubEndpointMappings.cs`
  **Acceptance**: Both methods accept `IEndpointRouteBuilder` only. No reference to `FleetOptions` or `RequireAuthorization` in this file. All route paths unchanged.

- [x] 4. Clean up `GitHubBackendPlugin.MapEndpoints` — remove FleetOptions resolution
  **What**: Update `MapEndpoints` to accept `IEndpointRouteBuilder builder` (matching the new interface). Remove the line `var fleetOptions = app.Services.GetRequiredService<FleetOptions>();`. Update calls to `GitHubEndpointMappings.MapAuthEndpoints(builder)` and `GitHubEndpointMappings.MapDataEndpoints(builder)` — passing just the builder. Remove the `using WeaveFleet.Application.Configuration;` import. Remove `using Microsoft.Extensions.DependencyInjection;` if no longer needed (check: `GetRequiredService` was the only usage — `serviceProvider.GetRequiredService<IUserContext>()` in `GetStatusAsync` also uses it, so keep it). Remove `using Microsoft.AspNetCore.Builder;` if no longer needed (check: no `WebApplication` reference remains, but `MapEndpoints` is the only usage — the parameter is now `IEndpointRouteBuilder` from `Microsoft.AspNetCore.Routing`, so add that using if needed).
  **Files**: `src/WeaveFleet.Infrastructure/Plugins/BuiltIn/GitHub/GitHubBackendPlugin.cs`
  **Acceptance**: `MapEndpoints` takes `IEndpointRouteBuilder`. No `FleetOptions` resolution. No `RequireAuthorization` call. Compiles cleanly.

- [x] 5. Update `GitHubPluginRegistrationTests` — fix StubBackendPlugin and test setup
  **What**: Update `StubBackendPlugin.MapEndpoints` to accept `IEndpointRouteBuilder builder` instead of `WebApplication app`. Change `app.MapGet(...)` calls inside the stub to `builder.MapGet(...)`. Update the test method: instead of calling `app.MapBackendPluginEndpoints()` directly on the `WebApplication`, the test needs to call it on an `IEndpointRouteBuilder`. Since `WebApplication` implements `IEndpointRouteBuilder`, the simplest fix is to just call `((IEndpointRouteBuilder)app).MapBackendPluginEndpoints()` or cast implicitly. Actually, since the extension method now extends `IEndpointRouteBuilder` and `WebApplication` implements it, `app.MapBackendPluginEndpoints()` will still compile and work — no change needed to the call site, only to the stub's `MapEndpoints` signature.
  **Files**: `tests/WeaveFleet.Api.Tests/Endpoints/GitHubPluginRegistrationTests.cs`
  **Acceptance**: Test compiles and passes. `StubBackendPlugin.MapEndpoints` takes `IEndpointRouteBuilder`. Routes are still discoverable in the test assertions.

- [x] 6. Verify all tests pass and no auth regressions
  **What**: Run the full test suite to confirm no regressions. The `GitHubEndpointAuthTests` are the critical ones — they verify that GitHub endpoints return 401 when auth is enabled and unauthenticated, and succeed when authenticated. These tests use `ApiWebApplicationFactory` which calls `MapFleetEndpoints()` → `MapBackendPluginEndpoints()` → plugin endpoints. Since the plugin endpoints are now mapped inside the authenticated `apiScope`, auth should work identically (or better — it's now centralized instead of plugin-managed).
  **Acceptance**: `dotnet test tests/WeaveFleet.Api.Tests` passes all tests. `dotnet build` succeeds for the entire solution.

## Verification
- [ ] `dotnet build` succeeds for the full solution (no compile errors)
- [ ] `dotnet test tests/WeaveFleet.Api.Tests` — all pass, especially:
  - `GitHubEndpointAuthTests.GitHubEndpoint_Returns401_WhenAuthEnabledAndUnauthenticated`
  - `GitHubEndpointAuthTests.GitHubEndpoint_DoesNotReturn401_WhenAuthEnabledAndAuthenticated`
  - `GitHubEndpointAuthTests.GitHubEndpoint_DoesNotReturn401_WhenAuthDisabled`
  - `GitHubPluginRegistrationTests.MapBackendPluginEndpoints_MapsGitHubCompatibilityRoutes`
- [ ] No `RequireAuthorization` calls in `src/WeaveFleet.Infrastructure/Plugins/` directory
- [ ] No `FleetOptions` references in `GitHubEndpointMappings.cs` or `GitHubBackendPlugin.cs`
- [ ] All `/api/integrations/github/...` routes still exist and have the same paths

## Potential Pitfalls

| Pitfall | Mitigation |
|---------|------------|
| **Double auth** — endpoints mapped inside `apiScope` already have `RequireAuthorization("FleetUser")`, but the GitHub plugin also added it. After cleanup, auth is applied once (by `apiScope`). | Removing plugin-level `RequireAuthorization` is the correct fix — the `apiScope` group already applies it to all child routes. Verify with existing `GitHubEndpointAuthTests`. |
| **`IEndpointRouteBuilder.ServiceProvider`** — `MapBackendPluginEndpoints` currently uses `app.Services` to resolve plugins. `IEndpointRouteBuilder` exposes `.ServiceProvider` instead. | Use `builder.ServiceProvider.GetServices<IBackendPlugin>()` in the updated method. |
| **Route path nesting** — `apiScope` is `app.MapGroup("/")`. Plugin endpoints use `/api/integrations/github/...`. Mapping plugin groups inside `apiScope` will produce `/` + `/api/integrations/github/...` = `/api/integrations/github/...`. No double-slash issue because ASP.NET normalizes this. | Verify with `GitHubPluginRegistrationTests` — route patterns should be unchanged. However, if using `MapGroup("/")` as the apiScope, the prefix is `/` which effectively adds nothing — routes are fine. |
| **`WebApplication` vs `IEndpointRouteBuilder` in tests** — `StubBackendPlugin` uses `app.MapGet(...)`. After changing to `builder.MapGet(...)`, the method resolution still works because `MapGet` is an extension on `IEndpointRouteBuilder`. | Straightforward — `MapGet` extends `IEndpointRouteBuilder`, which `WebApplication` implements. |
| **Unused `MapAuthenticatedGroup` helper** — after this refactor, it's no longer needed (it was already unused before — only the manual GitHub pattern called something similar). | Leave it in place — removing it is out of scope and it might be useful for non-plugin code later. Actually checking: it's not used anywhere currently. Could note for future cleanup but don't touch it here. |
| **Test `MapBackendPluginEndpoints` call** — the test calls `app.MapBackendPluginEndpoints()`. After the signature change to `IEndpointRouteBuilder`, this still compiles because `WebApplication` implements `IEndpointRouteBuilder`. But the return type changes from `WebApplication` to `IEndpointRouteBuilder`. Since the return value is not used in the test, this is fine. | No action needed — implicit interface implementation handles it. |
