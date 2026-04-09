# Fleet Plugin Infrastructure and GitHub-First Migration

## TL;DR
> **Summary**: Introduce a built-in plugin host in Fleet in two layers: frontend contribution infrastructure and backend plugin catalog/adapter infrastructure. Then migrate the existing GitHub integration onto those seams without changing current URLs, auth flows, or user-visible behavior.
> **Estimated Effort**: Large

## Context
### Original Request
Create an implementation plan under `.weave/plans/` for introducing plugin infrastructure into Fleet first, then migrating GitHub onto that infrastructure as the first plugin. The plan must be practical, incremental, and assume trusted built-in plugins with clean separation from core rather than runtime third-party loading.

### Key Findings
- The current frontend extension seam is still an integration-specific manifest/registry in `client/src/integrations/types.ts` and `client/src/integrations/registry.ts`, with GitHub self-registering via a side-effect import in `client/src/contexts/integrations-context.tsx`.
- Core shell files still hardcode GitHub as a first-class view:
  - `client/src/app.tsx`
  - `client/src/app/client-layout.tsx`
  - `client/src/components/layout/sidebar-icon-rail.tsx`
  - `client/src/components/layout/sidebar-panel.tsx`
  - `client/src/app/settings/page.tsx`
  - `client/src/components/settings/integrations-tab.tsx`
- GitHub UI is already clustered under `client/src/integrations/github/**`, which makes it a good first plugin migration target with wrappers rather than a big folder rewrite.
- The frontend status model is integration-centric and partly inconsistent with the backend: `client/src/hooks/use-integrations.ts` assumes generic host-level POST/DELETE `/api/integrations`, but the backend only exposes GET `/api/integrations` in `src/WeaveFleet.Api/Endpoints/FleetEndpoints.cs` plus GitHub-specific auth/data endpoints.
- Backend wiring is currently hardcoded around GitHub:
  - endpoint registration in `src/WeaveFleet.Api/Endpoints/EndpointExtensions.cs`
  - status response in `src/WeaveFleet.Api/Endpoints/FleetEndpoints.cs`
  - services in `src/WeaveFleet.Infrastructure/DependencyInjection.cs`
  - GitHub-specific endpoints/services in `src/WeaveFleet.Api/Endpoints/GitHubEndpoints.cs`, `src/WeaveFleet.Api/Endpoints/GitHubAuthEndpoints.cs`, `src/WeaveFleet.Infrastructure/Services/GitHubService.cs`, and `src/WeaveFleet.Infrastructure/Services/GitHubApiProxy.cs`
- Existing storage is generic enough to reuse as a plugin-scoped backing store: `src/WeaveFleet.Application/Services/IIntegrationStore.cs` and `src/WeaveFleet.Infrastructure/Services/FileIntegrationStore.cs`.
- Existing behavior that must remain stable during migration includes `/github` and `/github/:owner/:repo` routes, GitHub OAuth device flow, repo bookmarks, repo cache warming, and GitHub sidebar/settings behavior.

## Objectives
### Core Objective
Build a minimal built-in plugin platform for Fleet that cleanly separates host concerns from plugin concerns, then migrate GitHub to that platform as the first end-to-end plugin without breaking existing behavior or URLs.

### Deliverables
- [x] Frontend plugin infrastructure with host-owned types, registry, loader, runtime context, and slot-based contributions.
- [x] Backend plugin infrastructure with descriptors, status/catalog APIs, backend adapter contracts, plugin endpoint registration, and DI-based discovery for built-in plugins.
- [x] A minimal host/plugin contract and discovery model that supports trusted built-in frontend-only plugins and frontend+backend plugins.
- [x] Core shell rendering that consumes plugin contributions instead of importing GitHub UI directly.
- [x] A GitHub frontend plugin and GitHub backend adapter migrated onto the new contracts.
- [x] Compatibility shims that preserve current URLs and current GitHub flows while hardcoded wiring is removed.
- [x] Acceptance criteria, verification steps, and explicit non-goals that Tapestry can execute later.

### Definition of Done
- [x] `npm --prefix client typecheck`
- [x] `npm --prefix client test`
- [x] `dotnet test WeaveFleet.slnx`
- [x] `rg "MapGitHub(Auth)?Endpoints|MapGitHubEndpoints" src/WeaveFleet.Api/Endpoints src/WeaveFleet.Api/Program.cs` only shows plugin-owned registration or compatibility shims, not core endpoint wiring as the source of truth.
- [x] `rg "GitHubPage|GitHubRepoPage|GitHubPanel|GitHubRepoCacheWarmer" client/src/app.tsx client/src/app/client-layout.tsx client/src/components/layout client/src/app/settings/page.tsx` shows plugin wiring only, not hardcoded core rendering.
- [x] `GET /api/plugins` returns built-in plugin descriptors/status sourced from a backend plugin catalog.
- [x] Existing `/github` and `/github/:owner/:repo` routes still resolve and render the GitHub experience through plugin contributions.

### Guardrails (Must NOT)
- [x] Do not implement runtime third-party plugin loading, sandboxing, signed bundles, permission prompts, or an extension marketplace.
- [x] Do not move secrets, OAuth tokens, or GitHub API credentials into frontend plugin code.
- [x] Do not require a big-bang rename of every existing `integrations` file before host contracts are stable.
- [x] Do not let plugins directly control shell layout; they must contribute through host-owned slots and descriptors.
- [x] Do not break existing GitHub URLs, auth flow, bookmarks, or repo-browsing behavior during the migration.
- [x] Do not introduce background goroutines/services for plugin discovery without an explicit owner, cancellation path, and DI lifetime rationale.

## TODOs

- [x] 1. Define the minimal host/plugin contract and built-in discovery model
  **What**: Establish the canonical plugin model before moving any rendering or endpoint registration. On the frontend, define a `FleetPluginManifest` with metadata plus host-owned contribution slots for sidebar rail items, sidebar panels, routes, settings cards/sections, context resolvers, and optional startup hooks. On the backend, define a matching descriptor model with plugin id, display name, trust level, frontend presence, backend adapter presence, status shape, and endpoint registration entrypoint. Discovery should be compile-time/built-in only: a host-owned list of built-in plugin modules on the frontend, and DI-resolved built-in backend adapters on the server.
  **Files**: `client/src/plugins/types.ts`, `client/src/plugins/runtime.ts`, `client/src/plugins/registry.ts`, `client/src/plugins/loader.ts`, `client/src/plugins/builtin/index.ts`, `client/src/lib/api-types.ts`, `src/WeaveFleet.Application/Plugins/FleetPluginDescriptor.cs`, `src/WeaveFleet.Application/Plugins/PluginStatus.cs`, `src/WeaveFleet.Application/Plugins/IPluginCatalog.cs`, `src/WeaveFleet.Application/Plugins/IBackendPlugin.cs`
  **Acceptance**: The plan for execution can point to one minimal contract on each side, and both contracts explicitly support two cases: built-in frontend-only plugins and built-in plugins with backend adapters.

- [x] 2. Build frontend plugin infrastructure around slot-based contributions
  **What**: Replace the narrow integration registry with a plugin host layer that owns contribution ordering and composition. Add plugin registry helpers, a loader that registers built-in manifests, a runtime context/provider that exposes plugin descriptors/statuses to the UI, and slot query helpers such as `getSidebarViews()`, `getSidebarPanels()`, `getRoutes()`, `getSettingsSections()`, and `getStartupHooks()`. Keep the existing integration API temporarily available as a compatibility wrapper so GitHub can migrate incrementally.
  **Files**: `client/src/plugins/types.ts`, `client/src/plugins/registry.ts`, `client/src/plugins/loader.ts`, `client/src/plugins/runtime.ts`, `client/src/plugins/context.tsx`, `client/src/plugins/slots.ts`, `client/src/contexts/integrations-context.tsx`, `client/src/integrations/types.ts`, `client/src/integrations/registry.ts`
  **Acceptance**: Core UI code can query plugin contributions from host-owned slot APIs without importing GitHub manifests directly, and the temporary integration wrapper does not remain the source of truth.

- [x] 3. Introduce backend plugin infrastructure, catalog, and DI discovery
  **What**: Add a backend plugin layer that mirrors the frontend host model. Define a catalog service that aggregates built-in plugin descriptors and current status, an adapter interface for plugins that expose backend behavior, and DI registration that discovers all `IBackendPlugin` implementations from infrastructure. Add a host endpoint such as `/api/plugins` returning descriptors and status, then decide whether `/api/integrations` remains as a compatibility alias or is rewritten to proxy catalog output. Reuse the existing file-backed integration store behind a plugin-oriented abstraction instead of creating a second persistence mechanism.
  **Files**: `src/WeaveFleet.Application/Plugins/FleetPluginDescriptor.cs`, `src/WeaveFleet.Application/Plugins/PluginStatus.cs`, `src/WeaveFleet.Application/Plugins/IPluginCatalog.cs`, `src/WeaveFleet.Application/Plugins/IPluginStateStore.cs`, `src/WeaveFleet.Infrastructure/Plugins/BuiltInPluginCatalog.cs`, `src/WeaveFleet.Infrastructure/Plugins/PluginStateStore.cs`, `src/WeaveFleet.Infrastructure/DependencyInjection.cs`, `src/WeaveFleet.Api/Endpoints/PluginEndpoints.cs`, `src/WeaveFleet.Api/Endpoints/FleetEndpoints.cs`, `src/WeaveFleet.Api/Endpoints/EndpointExtensions.cs`
  **Acceptance**: Backend plugin metadata/status has one source of truth, DI can enumerate built-in backend plugins, and `FleetEndpoints.cs` no longer owns a GitHub-only status array.

- [x] 4. Define backend adapter registration and endpoint mapping seams
  **What**: Add the adapter interface details needed for built-in backend plugins to participate safely: descriptor access, status resolution, service registration hook if needed, and endpoint mapping hook that receives the app route builder. Keep this minimal and in-process: no isolated loading, no reflection-based package scanning, no background plugin host. Endpoint registration must remain explicit and startup-time so behavior stays understandable and testable. If a plugin later needs background work, require that its hosted service be registered through normal ASP.NET Core DI with clear lifecycle ownership.
  **Files**: `src/WeaveFleet.Application/Plugins/IBackendPlugin.cs`, `src/WeaveFleet.Infrastructure/DependencyInjection.cs`, `src/WeaveFleet.Api/Endpoints/EndpointExtensions.cs`, `src/WeaveFleet.Api/Program.cs`
  **Acceptance**: There is a clear execution path for “register built-in plugin in DI, include descriptor in catalog, and let plugin map its own endpoints” without any GitHub-specific special case in the host.

- [x] 5. Refactor the shell to render plugin contributions instead of hardcoded GitHub wiring
  **What**: Update the React shell so the host renders plugin-provided rail items, contextual panels, routes, settings cards/sections, and startup hooks from the plugin registry. Keep core-owned views (`welcome`, `fleet`, `repositories`, analytics/settings links) in host code, but add plugin slots around them. Replace the hardcoded `SidebarView` union with a host-compatible view id model, and rework path-to-view resolution so plugin routes can declare their owning view while preserving current `/github` route behavior.
  **Files**: `client/src/contexts/sidebar-context.tsx`, `client/src/components/layout/sidebar-icon-rail.tsx`, `client/src/components/layout/sidebar-panel.tsx`, `client/src/app.tsx`, `client/src/app/client-layout.tsx`, `client/src/app/settings/page.tsx`, `client/src/components/settings/integrations-tab.tsx`, `client/src/app/integrations/page.tsx`
  **Acceptance**: The shell no longer imports GitHub pages, panels, or cache warmers directly; instead it renders plugin-contributed route elements, panels, and startup hooks while existing navigation still works.

- [x] 6. Migrate GitHub frontend into the first built-in plugin
  **What**: Create a GitHub plugin entrypoint that declares all GitHub frontend contributions in one place: sidebar view metadata, sidebar panel component, `/github` and `/github/:owner/:repo` routes, settings contribution, context resolver, and startup repo-cache warmer. Keep current GitHub UI modules largely in place under `client/src/integrations/github/**` at first; use thin plugin wrappers so the migration is incremental and low-risk. Retire the side-effect registration path once the plugin loader owns registration.
  **Files**: `client/src/plugins/builtin/github/index.ts`, `client/src/plugins/builtin/github/plugin.ts`, `client/src/plugins/builtin/github/routes.tsx`, `client/src/plugins/builtin/github/runtime.tsx`, `client/src/integrations/github/index.ts`, `client/src/integrations/github/manifest.ts`, `client/src/app/github/page.tsx`, `client/src/app/github/[owner]/[repo]/_page-client.tsx`, `client/src/components/layout/github-panel.tsx`, `client/src/integrations/github/components/repo-cache-warmer.tsx`, `client/src/integrations/github/settings.tsx`
  **Acceptance**: GitHub is registered once through the built-in plugin loader, and all GitHub frontend surface area is exposed through plugin contributions rather than direct imports from core shell files.

- [x] 7. Migrate GitHub backend into the first backend plugin adapter
  **What**: Wrap current GitHub backend behavior in a backend plugin adapter that publishes the GitHub descriptor, reports connection status, and maps the existing GitHub auth/data endpoints. Preserve current endpoint paths under `/api/integrations/github/**` for compatibility, but shift ownership so the plugin adapter registers them. Keep `GitHubService` and `GitHubApiProxy` as plugin-internal services for now, but move them behind plugin-specific infrastructure folders as a follow-up step only if it stays low-risk.
  **Files**: `src/WeaveFleet.Infrastructure/Plugins/BuiltIn/GitHub/GitHubBackendPlugin.cs`, `src/WeaveFleet.Api/Endpoints/GitHubEndpoints.cs`, `src/WeaveFleet.Api/Endpoints/GitHubAuthEndpoints.cs`, `src/WeaveFleet.Infrastructure/Services/GitHubService.cs`, `src/WeaveFleet.Infrastructure/Services/GitHubApiProxy.cs`, `src/WeaveFleet.Infrastructure/DependencyInjection.cs`
  **Acceptance**: GitHub backend behavior is discoverable through `IBackendPlugin`/catalog infrastructure, current GitHub URLs stay stable, and all privileged GitHub operations remain server-side.

- [x] 8. Replace hardcoded integration status and connection assumptions with plugin-aware host behavior
  **What**: Update the frontend status fetch path and related API types so the host consumes plugin catalog data instead of an integration-specific shape. Resolve the current mismatch where `use-integrations.ts` assumes generic host-level connect/disconnect endpoints that do not exist. The practical path is to keep host-level status fetching generic, but make connection actions plugin-owned via descriptor-declared actions and GitHub-specific endpoints; then convert `IntegrationsContext` into a plugin-status/runtime provider with an integration-flavored compatibility API until callers are migrated.
  **Files**: `client/src/hooks/use-integrations.ts`, `client/src/contexts/integrations-context.tsx`, `client/src/lib/api-types.ts`, `src/WeaveFleet.Api/Endpoints/PluginEndpoints.cs`, `src/WeaveFleet.Api/Endpoints/FleetEndpoints.cs`
  **Acceptance**: Frontend status comes from plugin catalog data, stale assumptions about generic POST/DELETE `/api/integrations` are removed or redirected intentionally, and GitHub connection/disconnection still works through its existing endpoints.

- [x] 9. Add targeted tests and compatibility checks for the new host seams
  **What**: Add frontend tests for plugin registry/slot composition and shell rendering, backend tests for plugin catalog output and GitHub adapter registration, and compatibility tests proving `/github`, `/github/:owner/:repo`, and `/api/integrations/github/**` still work. Include a cleanup-oriented grep check so future work can confirm GitHub imports are no longer embedded in host shell files. Prefer focused unit/API tests first; add E2E coverage only for the route/sidebar path if the existing Playwright suite already has a stable place for it.
  **Files**: `client/src/plugins/__tests__/registry.test.ts`, `client/src/plugins/__tests__/slots.test.tsx`, `client/src/integrations/__tests__/registry.test.ts`, `tests/WeaveFleet.Api.Tests/Endpoints/PluginEndpointsTests.cs`, `tests/WeaveFleet.Api.Tests/Endpoints/GitHubPluginRegistrationTests.cs`, `tests/WeaveFleet.E2E/Pages/FleetSidebarPage.cs`, `tests/WeaveFleet.E2E/Tests/GoldenPathTests.cs`
  **Acceptance**: Tests cover plugin discovery, slot rendering, catalog/status output, GitHub endpoint registration, and route compatibility; grep-based cleanup checks can prove that core rendering files no longer directly wire GitHub.

- [x] 10. Plan explicit cleanup and naming convergence after GitHub is stable on the new seams
  **What**: Once GitHub runs fully through plugin infrastructure, remove or deprecate leftover `integration` naming that still implies the old host boundary. Keep this as a final cleanup pass, not a prerequisite: first make the plugin host real, then collapse wrappers and rename remaining types/files where that reduces confusion. Document which compatibility layers can remain temporarily and which must be deleted before calling the migration complete.
  **Files**: `client/src/integrations/types.ts`, `client/src/integrations/registry.ts`, `client/src/contexts/integrations-context.tsx`, `src/WeaveFleet.Application/Services/IIntegrationStore.cs`, `src/WeaveFleet.Infrastructure/Services/FileIntegrationStore.cs`
  **Acceptance**: The codebase has one canonical host/plugin vocabulary, or any remaining compatibility wrappers are explicitly marked transitional with clear follow-up deletion tasks.

## Verification
- [x] All tests pass
- [x] No regressions
- [x] `npm --prefix client typecheck`
- [x] `npm --prefix client test`
- [x] `dotnet test WeaveFleet.slnx`
- [x] `rg "GitHubPage|GitHubRepoPage|GitHubPanel|GitHubRepoCacheWarmer" client/src/app.tsx client/src/app/client-layout.tsx client/src/components/layout client/src/app/settings/page.tsx` shows plugin wiring only, not hardcoded shell rendering.
- [x] `rg "MapGitHub(Auth)?Endpoints|MapGitHubEndpoints" src/WeaveFleet.Api/Endpoints src/WeaveFleet.Api/Program.cs` shows plugin-owned registration or compatibility shims, not core startup as the source of truth.
- [x] `curl http://localhost:3000/api/plugins` returns built-in plugin descriptors including GitHub with frontend/backend flags and current status.
- [x] `curl http://localhost:3000/api/integrations/github/auth/status` still returns the current GitHub connection status shape.
- [x] Manual UI check: navigate to `/github` and `/github/{owner}/{repo}` from the sidebar and direct URL entry; behavior matches current Fleet before the refactor.
- [x] Non-goals remain out of scope: no runtime plugin loading, no third-party sandboxing, no new permissions model, and no breaking URL changes.
