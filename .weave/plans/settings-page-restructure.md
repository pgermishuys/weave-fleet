# Settings Page Restructure

## TL;DR
> **Summary**: Replace the 2-column grid settings page with a sidebar-navigated single-section view, repurpose the left ContextPanel for settings nav, and hide the right panel on the settings route.
> **Estimated Effort**: Medium

## Context
### Original Request
Restructure the settings page from an all-at-once 2-column grid into a sidebar-navigated single-section-at-a-time layout, hiding the irrelevant right panel.

### Key Findings
- **SettingsPage.vue** (640 lines) contains inline workspace settings + config overview alongside imported `CredentialsSection`, `AppearanceSection`, `SkillsSection`. Plugin sections rendered dynamically via `getSettingsSections()`.
- **ContextPanel.vue** resolves `activeRail === "settings"` to a placeholder component via `createPlaceholderPanel()`. Plugin panels can override this via `getSidebarPanels()`.
- **AppShell.vue** renders `RightPanel` / `CollapsedRightRail` unconditionally based on `rightPanelCollapsed`. No route-awareness.
- **sidebar.ts** store has `rightPanelCollapsed` with `setRightPanelCollapsed()`. No concept of "force hidden by route".
- Existing section components are self-contained (own data fetching, own UI). Workspace settings and config overview are inline in SettingsPage.vue.

## Objectives
### Core Objective
Settings page uses left sidebar for navigation, shows one section at a time in center, hides right panel.

### Deliverables
- [ ] Left sidebar shows settings navigation when `activeRail === "settings"`
- [ ] Center content renders one section at a time based on active selection
- [ ] Right panel hidden on settings page
- [ ] Workspace settings extracted to own component
- [ ] Config overview extracted to own component

### Definition of Done
- [ ] `npm run build` succeeds with no type errors
- [ ] All existing settings functionality preserved (credentials CRUD, workspace roots CRUD, theme selection, skills install/remove, plugin sections, config overview)
- [ ] Navigating to settings hides right panel; leaving settings restores previous state

### Guardrails (Must NOT)
- Do not change the `/settings` route path
- Do not alter existing section component APIs (CredentialsSection, AppearanceSection, SkillsSection)
- Do not break plugin settings section registration

## TODOs

- [x] 1. Extract Workspace Settings Section
  **What**: Move the inline workspace settings template + logic (workspace label, preferred root, auto-refresh toggle, configured roots list, add root form) from SettingsPage.vue into a new `WorkspaceSection.vue` component. Keep the same reactive state pattern (workspace preferences, workspace roots API calls).
  **Files**: `client/src/components/settings/WorkspaceSection.vue`, `client/src/components/settings/SettingsPage.vue`
  **Acceptance**: WorkspaceSection renders identically to current inline workspace settings. SettingsPage imports and uses it.

- [x] 2. Extract Config Overview Section
  **What**: Move the inline config overview template + computed properties (paths, agent count, provider count, model count, provider status list) into a new `ConfigOverviewSection.vue`. It receives data via `useConfig()` composable directly.
  **Files**: `client/src/components/settings/ConfigOverviewSection.vue`, `client/src/components/settings/SettingsPage.vue`
  **Acceptance**: ConfigOverviewSection renders identically to current inline config overview.

- [x] 3. Create Settings Navigation Panel
  **What**: Create `SettingsNavPanel.vue` — a left sidebar panel listing settings categories. Each item has an icon, label, and emits a selection event. Categories: General (workspace label, preferred root, auto-refresh), Workspace Roots, Credentials, Appearance, Skills, Plugins (only if plugin sections exist), System (config overview). Use the same `context-panel__content` CSS patterns from ContextPanel for consistency. Track active section via a `useSettingsNavStore` or a simple `provide/inject` from SettingsPage.
  **Files**: `client/src/components/settings/SettingsNavPanel.vue`
  **Acceptance**: Panel renders a vertical nav list with icons and labels. Clicking an item highlights it.

- [x] 4. Wire Settings Nav Panel into ContextPanel
  **What**: In `ContextPanel.vue`, replace the placeholder panel for `"settings"` rail with the new `SettingsNavPanel`. Import it and add to `panelComponents` map. Remove `"settings"` from the `pluginPanelRegistry` array so it no longer generates a placeholder.
  **Files**: `client/src/components/layout/ContextPanel.vue`
  **Acceptance**: When `activeRail === "settings"`, left sidebar shows the settings navigation instead of placeholder text.

- [x] 5. Add Active Settings Section State
  **What**: Create a composable `useSettingsNav` (or add to sidebar store) that tracks which settings section is active. Use `provide/inject` pattern: SettingsPage provides the reactive section ID, SettingsNavPanel reads and writes it. Default to `"general"`. Type: `"general" | "workspace-roots" | "credentials" | "appearance" | "skills" | "plugins" | "system"`.
  **Files**: `client/src/composables/use-settings-nav.ts`
  **Acceptance**: Composable exports `activeSection` ref and `setActiveSection` function. Usable from both nav panel and settings page.

- [x] 6. Refactor SettingsPage to Single-Section View
  **What**: Replace the 2-column grid layout with a single-column layout that conditionally renders only the active section. Remove the header card (settings icon + description) or keep it minimal. Use `v-if` / dynamic `<component :is>` keyed on `activeSection`. Map section IDs to components: `general` → inline general fields (workspace label, preferred root, auto-refresh — extract from WorkspaceSection or keep separate), `workspace-roots` → WorkspaceSection (roots list + add form only), `credentials` → CredentialsSection, `appearance` → AppearanceSection, `skills` → SkillsSection, `plugins` → plugin sections loop, `system` → ConfigOverviewSection.
  **Files**: `client/src/components/settings/SettingsPage.vue`
  **Acceptance**: Only one section visible at a time. Switching via nav panel changes displayed section. All functionality preserved.

- [x] 7. Hide Right Panel on Settings Page
  **What**: In `AppShell.vue`, add route-awareness to conditionally hide the right panel (both `RightPanel` and `CollapsedRightRail`) when on the settings route. Use `useRoute()` to check `route.name === "settings"` or `route.path === "/settings"`. Save and restore `rightPanelCollapsed` state when entering/leaving settings. Alternative: add a `rightPanelHidden` computed in sidebar store that AppShell checks.
  **Files**: `client/src/components/layout/AppShell.vue`, `client/src/stores/sidebar.ts`
  **Acceptance**: Right panel (expanded or collapsed rail) not visible on settings page. Navigating away restores previous right panel state.

- [x] 8. Split General vs Workspace Roots
  **What**: The "General" section should contain only workspace label, preferred root dropdown, and auto-refresh toggle. The "Workspace Roots" section should contain the configured roots list, refresh button, and add-root form. This may mean splitting WorkspaceSection.vue into two, or having WorkspaceSection accept a `mode` prop. Simpler: create `GeneralSection.vue` with just the preferences fields, keep `WorkspaceSection.vue` for roots management.
  **Files**: `client/src/components/settings/GeneralSection.vue`, `client/src/components/settings/WorkspaceSection.vue`
  **Acceptance**: General and Workspace Roots render as distinct sections with no overlap.

## Verification
- [x] `npm run build` passes with zero errors
- [x] `npm run lint` passes
- [x] All 7 settings sections accessible via left nav
- [x] Plugin settings sections still render dynamically
- [x] Right panel hidden on settings, visible elsewhere
- [x] No regressions in credentials, workspace roots, theme, skills functionality
