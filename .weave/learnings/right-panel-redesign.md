# Learnings — right-panel-redesign

## Pre-existing E2E infrastructure drift on main (blocked Task 22)

Three independent E2E-blocking issues on `main` predate this plan. Two were fixed here; one remains.

1. **DI startup failure (commit `e63a67b`) — FIXED here**. `src/WeaveFleet.Infrastructure/DependencyInjection.cs` line 300 registers `IHarnessPoolRecycler` via a factory that calls `sp.GetRequiredService<OpenCodeHarnessRuntime>()`. `FleetWebApplicationFactory.RemoveProductionHarnessRegistrations` removed `OpenCodeHarnessRuntime` but not `IHarnessPoolRecycler`, so the recycler factory threw "No service for type OpenCodeHarnessRuntime has been registered" during host build. Every E2E test failed before running. Fix applied: added `ReplaceHarnessPoolRecycler` helper + `NoOpHarnessPoolRecycler` stub to both `FleetWebApplicationFactory.cs` and `AuthFleetWebApplicationFactory.cs`.

2. **UI-vs-test drift for new-session flow (commit `82af02e feat(client): replace new session modal with inline form`) — page objects updated here, runtime not verified**. `FleetDashboard.vue`'s "New Session" button now navigates to route `/sessions/new` and mounts `NewSessionForm.vue` inline. `NewSessionDialog.vue` is dead code but still on disk. The E2E page object `FleetDashboardPage.ClickNewSessionAsync()` still returned `NewSessionDialog` and waited for `[data-testid="new-session-dialog"]`. This plan updated `ClickNewSessionAsync` to navigate to `/sessions/new` and return a new `NewSessionFormPage` that targets the inline form. Added `data-testid="new-session-form"` to `NewSessionForm.vue` root.

3. **Missing testid at runtime — UNRESOLVED**. Even after rebuilding the frontend and confirming the compiled bundle (`sessions.new-GyW5jA97.js` in `src/WeaveFleet.Api/wwwroot/assets`) contains `data-testid":"new-session-form"`, the rendered DOM at `/sessions/new` shows only `class` on that root div — the `data-testid` attribute is absent. Investigated via runtime diagnostics: parent chain confirms `NewSessionForm.vue`'s root div is mounted (`create-session-submit` button is present below it, classes match), but `data-testid` never lands in the DOM. Not a stale-asset issue (verified index.html references the new hash). No custom Vue template compiler options in `vite.config.ts`. Root cause remains unknown as of session end. This bug blocks every E2E test that goes through session creation (54/91 previously; possibly all after this plan's E2E test additions).

## Test-harness limitation: no diff/artifact seeding

`TestScenarioBuilder` cannot seed session diffs or a mirrored visual artifact payload. This blocks a full E2E test of `__visual__/plan.md` routing (artifact chip and conversation `plan.md` link → content slot). Unit coverage in `client/src/composables/__tests__/use-file-browser.test.ts` (synthetic-path early-out, positive + negative branches) and `use-content-panel.test.ts` (`selectFile(path, isChangedFile=true)` → `"changes"` tab) compensates for the missing E2E coverage.

## VP4 payload state must not delete map entries on clear

`useVisualPanel.clearVisual` originally deleted the session's map entry. This orphaned every `ShallowRef` already captured by consumer `computed(() => useVisualPanel(sessionId))` blocks: after clear, a subsequent `useVisualPanel(sessionId)` call created a NEW ref while existing consumers still held the old one, so later `showVisual` writes stopped propagating to already-rendered panels. Fix: only null the ref (`entry.value = null`), never delete the key. Regression test at `client/src/composables/__tests__/use-visual-panel.test.ts` lines 53-71.

## Synthetic path derivation must be shared

`SessionMetadataHeader.vue` and `SessionsV2RightPanel.vue` each derived `__visual__/<name>` independently and disagreed on the fallback field order, causing selection-highlight desync. Extracted `getVisualPayloadSyntheticPath` into `client/src/lib/visual-payload-path.ts` and used from both sites.

## Vite E2E frontend copy behavior

The `WeaveFleet.E2E.csproj` has an `EnsureFrontendBuilt` `InitialTargets` step that runs `bun install --frozen-lockfile && bun run build` before project references resolve. `WeaveFleet.Api.csproj` then copies `client/dist/` into `src/WeaveFleet.Api/wwwroot/`. Iteration with `-p:SkipFrontendBuild=true` reuses whatever `wwwroot` state was last written — old asset chunks accumulate under `wwwroot/assets/` because Vite doesn't clear the destination outside of `client/dist/`.
