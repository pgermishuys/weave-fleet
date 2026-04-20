# Dedicated Plugin Configuration Pages

## TL;DR
> **Summary**: Add a `/settings/plugins/:pluginId` route with a shared shell component so each plugin can declare a dedicated config page. Marketplace "Configure" navigates there instead of alerting.
> **Estimated Effort**: Short

## Context
### Original Request
Plugins need their own dedicated configuration place. Currently `MarketplacePanel.vue` `handleConfigure()` just calls `window.alert()`. GitHub already has a full `GitHubSettings.vue` component contributed via `settingsSections`, rendered inline within `SettingsPage.vue`.

### Key Findings
- **Router**: TanStack file-based routing (`client/src/routes/`). Settings lives at `/settings` → `SettingsPage.vue`.
- **Plugin types**: `FleetPluginContributions` already has `settingsSections` (inline sections in settings page). No concept of a standalone config page.
- **Slots system**: `client/src/plugins/slots.ts` aggregates contributions from registered manifests.
- **GitHub plugin**: `client/src/plugins/builtin/github/index.ts` exports manifest with `settingsSections` pointing to `GitHubSettings.vue`.
- **Marketplace**: `client/src/plugins/builtin/marketplace/MarketplacePanel.vue` has hardcoded plugin list; `handleConfigure` is a stub.

## Objectives
### Core Objective
Give each plugin a dedicated, routable configuration page accessible from Marketplace and settings navigation.

### Deliverables
- [x] New route `/settings/plugins/$pluginId`
- [x] `PluginConfigShell.vue` shared wrapper component
- [x] New `configPage` contribution type in plugin manifest
- [x] Marketplace "Configure" navigates to the route
- [x] GitHub plugin migrated as reference implementation

### Definition of Done
- [x] Clicking "Configure" on GitHub in Marketplace navigates to `/settings/plugins/github`
- [x] The page renders `GitHubSettings.vue` inside `PluginConfigShell`
- [x] A plugin without `configPage` still works (graceful fallback)

### Guardrails (Must NOT)
- Do NOT remove existing `settingsSections` — they remain for inline settings
- Do NOT change the global settings page layout
- Do NOT introduce a new router instance or break file-based routing

## TODOs

- [x] 1. Extend plugin types with `configPage` contribution
  **What**: Add `FleetPluginConfigPage` interface and optional `configPage` field to `FleetPluginContributions`.
  **Files**: `client/src/plugins/types.ts`
  **Acceptance**: Type compiles; existing manifests unaffected.

  ```ts
  export interface FleetPluginConfigPage {
    component: Component;
    /** Optional sub-sections/tabs within the config page */
    sections?: readonly { id: string; label: string; component: Component }[];
  }
  ```

  Add to `FleetPluginContributions`:
  ```ts
  configPage?: FleetPluginConfigPage;
  ```

- [x] 2. Add slot accessor for config pages
  **What**: Add `getConfigPage(pluginId)` helper to slots.
  **Files**: `client/src/plugins/slots.ts`
  **Acceptance**: Returns the `configPage` contribution for a given plugin ID or `undefined`.

- [x] 3. Create `PluginConfigShell.vue`
  **What**: Shared layout component that receives `pluginId` param, resolves the manifest, renders header (plugin icon + name + status badge + back link to settings) and the plugin's `configPage.component` via `<component :is>`.
  **Files**: `client/src/components/settings/PluginConfigShell.vue`
  **Acceptance**: Shows plugin name, back navigation, renders child component. Shows 404-style message if plugin has no `configPage`.

  Data it should render:
  - Plugin `displayName` and `icon` (from sidebar item or descriptor)
  - Connection status badge (from `usePluginRuntime`)
  - Back link → `/settings`
  - The `configPage.component` slot

- [x] 4. Create route file for `/settings/plugins/$pluginId`
  **What**: TanStack file-based route that renders `PluginConfigShell`.
  **Files**: `client/src/routes/settings.plugins.$pluginId.tsx`
  **Acceptance**: Route resolves; `pluginId` param available. Run `npx tsr generate` to regenerate route tree.

  ```tsx
  import { createFileRoute } from "@tanstack/vue-router";
  import PluginConfigShell from "@/components/settings/PluginConfigShell.vue";

  export const Route = createFileRoute("/settings/plugins/$pluginId")({
    component: PluginConfigShell,
  });
  ```

- [x] 5. Wire Marketplace "Configure" to navigate
  **What**: Replace `window.alert` in `MarketplacePanel.vue` with `router.navigate({ to: '/settings/plugins/$pluginId', params: { pluginId } })`.
  **Files**: `client/src/plugins/builtin/marketplace/MarketplacePanel.vue`
  **Acceptance**: Clicking Configure navigates to the plugin config page.

- [x] 6. Migrate GitHub plugin to declare `configPage`
  **What**: Add `configPage: { component: GitHubSettings }` to the GitHub manifest. Keep `settingsSections` for backward compat (can remove later).
  **Files**: `client/src/plugins/builtin/github/index.ts`
  **Acceptance**: `/settings/plugins/github` renders the full GitHub auth flow.

## Verification
- [x] `npx tsr generate` succeeds (route tree regenerates)
- [x] TypeScript compiles without errors
- [x] Navigating to `/settings/plugins/github` renders GitHubSettings
- [x] Navigating to `/settings/plugins/nonexistent` shows graceful fallback
- [x] Marketplace Configure button navigates correctly
- [x] Existing `/settings` page still renders plugin sections inline
