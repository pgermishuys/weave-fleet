# Learnings: Fleet Plugin Infrastructure and GitHub-First Migration

## Task 1: Define the minimal host/plugin contract and built-in discovery model
- **Discrepancy**: The plan implied plugin routes could be modeled as a simple interface extension, but `react-router` exposes `RouteObject` as a union type.
- **Resolution**: Defined the frontend plugin route contract as an intersection type alias instead of an interface inheritance chain.
- **Suggestion**: Call out `react-router` route typing constraints in the plan when frontend route contribution contracts are part of the task.

## Task 2: Build frontend plugin infrastructure around slot-based contributions
- **Discrepancy**: The existing frontend integration registry was still the only source of runtime metadata for settings and context resolution, so replacing it outright would have broken current GitHub consumers too early.
- **Resolution**: Made the plugin registry the source of truth while keeping the integration registry as an ordered compatibility view backed by plugin registration.
- **Suggestion**: Explicitly note when a planned compatibility wrapper must remain readable by legacy callers but should derive from the new host registry instead of duplicating state.

## Task 3: Introduce backend plugin infrastructure, catalog, and DI discovery
- **Discrepancy**: The existing persistence seam was named around integrations, but GitHub runtime state and bookmarks already depended on it directly across host and plugin code.
- **Resolution**: Introduced a plugin-oriented state store adapter over the existing integration store so the new catalog and GitHub plugin can share one persistence source without forcing an immediate rename.
- **Suggestion**: When reuse of an old abstraction is expected, the plan should state whether to wrap it or rename it during the task to avoid ambiguity about transitional ownership.

## Task 4: Define backend adapter registration and endpoint mapping seams
- **Discrepancy**: Putting ASP.NET endpoint mapping primitives into the application-layer contract created unnecessary cross-project coupling and made the first implementation awkward.
- **Resolution**: Kept the backend plugin contract minimal with a startup-time `MapEndpoints(WebApplication app)` hook and moved concrete GitHub endpoint registration logic into infrastructure-owned mappings.
- **Suggestion**: The plan should explicitly say whether endpoint mapping hooks belong in the application contract or may depend on ASP.NET host types directly for built-in plugins.

## Task 5: Refactor the shell to render plugin contributions instead of hardcoded GitHub wiring
- **Discrepancy**: Existing sidebar and route helpers had unit tests that assumed a fixed GitHub-aware API surface, so a clean plugin-driven refactor still had to preserve those helper contracts during the transition.
- **Resolution**: Switched the shell to plugin-contributed routes, panels, and startup hooks while keeping compatibility exports like `viewHasPanel`, `viewForPathname`, and `nextViewForSwitch` usable by current tests.
- **Suggestion**: Mention when host helper APIs are externally exercised by tests so the migration can preserve or intentionally rewrite them in the same task.

## Task 6: Migrate GitHub frontend into the first built-in plugin
- **Discrepancy**: The only remaining GitHub self-registration path was a small side-effect module, but leaving it in place would have kept duplicate registration semantics alive after the plugin loader became authoritative.
- **Resolution**: Added a dedicated built-in GitHub plugin entrypoint for routes, panel, settings, startup hook, and context resolution, then removed the side-effect registration from `client/src/integrations/github/index.ts`.
- **Suggestion**: The plan should explicitly call out any legacy side-effect modules that must be neutered once the built-in plugin loader takes ownership.

## Task 7: Migrate GitHub backend into the first backend plugin adapter
- **Discrepancy**: The frontend still depended on generic `connect` and `disconnect` helpers even though the backend only had GitHub-specific auth routes.
- **Resolution**: Exposed plugin-declared connect/disconnect actions from the catalog, added a GitHub token-connect endpoint, and updated the shared integrations hook to execute plugin-owned actions instead of imaginary host-level POST/DELETE endpoints.
- **Suggestion**: The plan should explicitly identify when frontend action assumptions must be replaced by descriptor-driven actions in the same step as backend plugin migration.

## Task 8: Replace hardcoded integration status and connection assumptions with plugin-aware host behavior
- **Discrepancy**: The old `/api/integrations` response shape was still embedded in frontend polling code and context assembly, even after plugin catalog data existed.
- **Resolution**: Switched the polling hook to `/api/plugins`, derived compatibility integration status objects from plugin descriptors/statuses, and left `/api/integrations` as a compatibility alias backed by the catalog.
- **Suggestion**: The plan should clearly state which legacy endpoints remain as aliases after the host switches to plugin catalog data so cleanup scope is obvious.

## Task 9: Add targeted tests and compatibility checks for the new host seams
- **Discrepancy**: There were no existing plugin-specific test files despite the plan listing them, and the first new API tests needed endpoint-route assertions instead of full application bootstrapping.
- **Resolution**: Added focused frontend registry/slot tests and backend endpoint-registration tests that validate the new seams with minimal setup.
- **Suggestion**: Where the plan names brand-new test files, it should also note the preferred test style (route registration, hook behavior, or full integration) to reduce guesswork.

## Task 10: Plan explicit cleanup and naming convergence after GitHub is stable on the new seams
- **Discrepancy**: Full integration-to-plugin renaming was still too broad for this pass because several legacy callers and persisted data paths intentionally remain in place for compatibility.
- **Resolution**: Marked the remaining integration-named frontend and storage abstractions as transitional in code comments so the canonical plugin vocabulary is clear without forcing a risky big-bang rename.
- **Suggestion**: The plan should distinguish between "rename now" and "mark transitional now, rename later" cleanup items when compatibility layers are expected to survive the migration.
