# Learnings: dedicated-plugin-config-pages

## Task 1: Extend plugin types with `configPage` contribution
- **Discrepancy**: The plan sketched `FleetPluginConfigPage` with `sections`, but the repo's current plugin contribution style aligned better with a minimal page contract.
- **Resolution**: Added an additive `configPage` interface with `title`, `component`, and optional `icon`, plus `configPage?: FleetPluginConfigPage` on `FleetPluginContributions`.
- **Suggestion**: Update the plan to describe the config page shape as a minimal shell-driven contract first, and treat nested sections/tabs as a later enhancement.

## Task 4: Create route file for `/settings/plugins/$pluginId`
- **Discrepancy**: The plan referenced `settings.plugins.$pluginId.tsx`, but this repo's TanStack file routing would nest that under `settings.tsx`, which is a leaf route without an outlet.
- **Resolution**: Added `client/src/routes/settings_.plugins.$pluginId.tsx` using `createFileRoute("/settings_/plugins/$pluginId")`, which preserves the user-facing URL `/settings/plugins/$pluginId` while keeping the route unnested.
- **Suggestion**: When proposing nested settings subroutes in this repo, explicitly account for TanStack's `_` unnesting convention or plan a layout-route conversion first.
