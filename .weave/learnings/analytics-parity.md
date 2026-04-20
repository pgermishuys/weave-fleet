# Learnings: Analytics Parity

## Task 1: use-analytics-sessions
- **Discrepancy**: The plan referenced `src/composables/use-analytics-sessions.ts`, but the actual file is `client/src/composables/use-analytics-sessions.ts`; the hard `limit=5` was not in the composable and was instead applied from `client/src/components/analytics/AnalyticsPage.vue`.
- **Resolution**: Updated the real composable path to accept optional `sortBy` and `sortDir`, and removed the page-level session limit call-site.
- **Suggestion**: Record workspace-relative paths under `client/` and note page-level callers when a behavior is enforced outside the target composable.

## Task 2: use-analytics-models
- **Discrepancy**: The plan again referenced `src/composables/...`, but the actual source lives under `client/src/composables/...`.
- **Resolution**: Added optional `projectId` support in `client/src/composables/use-analytics-models.ts` and preserved the existing fetch contract.
- **Suggestion**: Normalize plan file paths to the real app root to reduce delegation churn and verification overhead.

## Task 3: use-analytics-filters
- **Discrepancy**: The plan's file path again omitted the `client/` app root, and the dropdown source needed to be derived from summary data already available in the frontend rather than introducing a new API call.
- **Resolution**: Exposed `topProjects` from `client/src/composables/use-analytics-filters.ts` by composing the existing analytics summary data with the persisted date filters.
- **Suggestion**: For composable enhancement tasks, note whether the required data should come from existing frontend composition versus a direct endpoint call.

## Task 4: StatCard.vue
- **Discrepancy**: The planned component path omitted the `client/` prefix; the app already uses shared UI card primitives that better match the existing visual language than bespoke card markup.
- **Resolution**: Created `client/src/components/analytics/cards/StatCard.vue` using typed props and the existing card primitives/tokens.
- **Suggestion**: When adding shared UI atoms, note whether they should compose existing design-system primitives.

## Task 5: HorizontalCostBars.vue
- **Discrepancy**: The planned component path omitted the `client/` prefix; the component also benefited from an explicit empty state and internal ranking/sorting behavior not spelled out in the plan.
- **Resolution**: Created `client/src/components/analytics/charts/HorizontalCostBars.vue` with typed props, descending cost ranking, relative-width bars, and empty-state handling.
- **Suggestion**: For reusable visualization atoms, specify whether sorting/empty-state behavior should live inside the component.

## Task 6: AnalyticsFilters.vue
- **Discrepancy**: The filter extraction could not be completed in isolation because the inline filter markup and project suggestion source still lived in `AnalyticsPage.vue`.
- **Resolution**: Created `client/src/components/analytics/AnalyticsFilters.vue` and updated `client/src/components/analytics/AnalyticsPage.vue` to pass persisted filter state and `topProjects` into the new typed dropdown-based component.
- **Suggestion**: When a component-extraction task requires replacing inline page markup, include the host page file in the plan's file list.

## Task 7: AnalyticsTabs.vue
- **Discrepancy**: The plan described a simple tab bar but did not specify whether to use existing generic tab primitives or a bespoke presentational control.
- **Resolution**: Created `client/src/components/analytics/AnalyticsTabs.vue` as a focused presentational component with an explicit typed `activeTab`/`select` contract and exported tab ids for later page integration.
- **Suggestion**: For shell components, specify whether host pages should reuse exported types/contracts and whether generic UI primitives are preferred.

## Task 8: OverviewTab.vue
- **Discrepancy**: The overview task needed one shared chart/list component extension because cost-bar rows also needed optional secondary detail text for estimated cost and tokens.
- **Resolution**: Created `client/src/components/analytics/tabs/OverviewTab.vue` and extended `client/src/components/analytics/charts/HorizontalCostBars.vue` with optional row detail support.
- **Suggestion**: When shared atoms need richer presentation in downstream tabs, include that extension explicitly in the plan to avoid hidden cross-file scope.

## Task 9: ProjectsTab.vue
- **Discrepancy**: The plan did not mention empty-state handling or whether project cards should internally rank/sort the incoming project list.
- **Resolution**: Created `client/src/components/analytics/tabs/ProjectsTab.vue` with internal cost-desc sorting and a dedicated empty state.
- **Suggestion**: For tab views backed by summary arrays, specify whether ordering is pre-sorted by the parent or should be enforced inside the component.

## Task 10: SessionsTab.vue
- **Discrepancy**: The sessions view needed an explicit parent/child sort contract even though the plan only mentioned sortable numeric columns, and loading/empty states were also required for practical host-page integration.
- **Resolution**: Created `client/src/components/analytics/tabs/SessionsTab.vue` with typed sort props/emits, internal sorting, and loading/empty state handling.
- **Suggestion**: For table tabs, include whether sorting is local or server-driven and list required state props (loading, error, empty) up front.

## Task 11: ModelsTab.vue
- **Discrepancy**: The models view also required explicit loading/empty state props and relied on the shared `HorizontalCostBars` component rather than a separate dedicated chart component from the original map.
- **Resolution**: Created `client/src/components/analytics/tabs/ModelsTab.vue` as a prop-driven tab using `HorizontalCostBars` for the horizontal cost chart plus a detailed analytics table.
- **Suggestion**: Clarify when a proposed dedicated chart component can be replaced by an existing shared visualization atom.

## Task 12: AnalyticsPage.vue shell rewrite
- **Discrepancy**: The page-shell task also needed to own sessions sort state and route different loading/error/empty props into each extracted tab, which was more orchestration detail than the plan spelled out.
- **Resolution**: Rewrote `client/src/components/analytics/AnalyticsPage.vue` as a thin shell that composes filters, tabs, dynamic tab rendering, and the analytics composables with explicit prop wiring.
- **Suggestion**: For shell rewrite tasks, call out any state the shell must retain locally (for example active tab and sort state) after decomposition.

## Task 13: Remove legacy single-page chart code
- **Discrepancy**: By the time this task was reached, the legacy flat-page chart/list implementation had already been fully removed as part of the shell rewrite, so no additional code change was necessary.
- **Resolution**: Verified `client/src/components/analytics/AnalyticsPage.vue` was shell-only and contained no dead inline chart/list logic, imports, or helpers.
- **Suggestion**: Mark cleanup-only tasks as verification checkpoints when they are expected to be satisfied by an immediately preceding refactor task.

## Task 14: Preserve `/analytics` route
- **Discrepancy**: The route-preservation task was verification-only because no route files had been intentionally changed during the refactor.
- **Resolution**: Verified `client/src/routes/analytics.tsx` and generated route metadata still resolve `/analytics` to `client/src/components/analytics/AnalyticsPage.vue` without path changes.
- **Suggestion**: Mark route-stability tasks as verification-only when no route-level edits are expected.

## Task 15: Verify build/typecheck
- **Discrepancy**: `npm run build` initially passed while repo-wide type errors remained in unrelated session/dashboard files outside the analytics scope, so the verification item was not satisfied on the first pass.
- **Resolution**: Fixed the remaining source-level TypeScript issues in the affected session/dashboard files, then re-ran `npm run typecheck` and `npm run build` successfully.
- **Suggestion**: Separate analytics-specific verification from pre-existing global typecheck debt, or explicitly budget a remediation task for unrelated failures.

## Task 16: Verify all tabs render
- **Discrepancy**: Render verification relied on shell wiring/build evidence rather than a dedicated runtime test harness because the repo's global test/typecheck state is already degraded.
- **Resolution**: Verified the route, tab mapping, dynamic component rendering, and per-tab prop wiring; no analytics rendering defects were found.
- **Suggestion**: Add a lightweight analytics smoke test to make tab render verification explicit.

## Task 17: Verify filter propagation
- **Discrepancy**: Verification uncovered a real propagation bug: the filter dropdown's `topProjects` source was not being refetched when `from`/`to` changed, even though the tab data refreshed correctly.
- **Resolution**: Updated `client/src/composables/use-analytics-filters.ts` to watch the persisted date filters and refetch the summary backing `topProjects`.
- **Suggestion**: Include dropdown-option refresh behavior in filter propagation acceptance criteria, not just tab data updates.

## Task 18: Verify no regressions on other routes
- **Discrepancy**: Regression verification found one shared navigation-state issue outside route files proper: `/analytics` was not classified with the sessions rail in shared navigation helpers.
- **Resolution**: Updated `client/src/composables/use-commands.ts` and `client/src/components/layout/IconRail.vue` so analytics navigation keeps the shared rail/context state aligned.
- **Suggestion**: Include shared navigation-state helpers in route-regression verification scope whenever a new top-level route surface is added or repurposed.
