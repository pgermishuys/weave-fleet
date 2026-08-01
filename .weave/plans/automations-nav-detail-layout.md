# Automations Nav + Detail Layout Refactor

## TL;DR
Refactor the Automations page from a single-page card list with modal dialogs to a Settings-style left nav panel + right detail view, following the exact same patterns as `SettingsNavPanel.vue` / `SettingsPage.vue` / `use-settings-nav.ts`.

## Context
The Settings route uses a `[settings-menu][settings-detail]` layout: `SettingsNavPanel.vue` renders in the `ContextPanel`, and `SettingsPage.vue` renders the detail for the active section. The Automations route currently maps to `SessionsPanel` in `ContextPanel.vue` (placeholder) and renders everything in `AutomationsPage.vue` with modal dialogs for create/edit.

Key pattern files:
- `client/src/composables/use-settings-nav.ts` — module-level `ref` shared composable
- `client/src/components/settings/SettingsNavPanel.vue` — left nav with `modelValue` / `update:modelValue`
- `client/src/components/settings/SettingsPage.vue` — conditional rendering based on `activeSection`
- `client/src/components/layout/ContextPanel.vue` — maps rail IDs to panel components

Existing automation files:
- `client/src/composables/use-automations.ts` — CRUD composable (keep as-is)
- `client/src/components/automations/AutomationCard.vue` — card component (will be simplified to nav item)
- `client/src/components/automations/AutomationForm.vue` — form component (reused inline)
- `client/src/components/pages/AutomationsPage.vue` — current page (major refactor)

## Scope
- In scope:
  - New `AutomationsNavPanel.vue` for the context panel
  - New `use-automations-nav.ts` composable for selected automation / view state
  - New `AutomationDetailPanel.vue` for the right-side detail view
  - Refactor `AutomationsPage.vue` to use detail panel instead of modals
  - Wire `ContextPanel.vue` to use `AutomationsNavPanel` for the `automations` rail
  - Update `AGENTS.md` layout table for automations route: `[automations-list][automations-detail]`
- Out of scope:
  - Changes to `use-automations.ts` CRUD logic
  - Changes to `AutomationForm.vue` internals
  - Backend / API changes
  - Resize gutter between automations panels
- Constraints / assumptions:
  - Follow Settings pattern exactly (module-level ref, `modelValue` prop pattern)
  - Delete confirmation can remain as `AlertDialog` (inline, not full-page)
  - "New automation" should be a nav item or button in the nav panel that switches to a create view in the detail area

## Objectives
- Automations route renders `AutomationsNavPanel` in the context panel (left) and detail view (right)
- Selecting an automation in the nav shows its details inline (no modal)
- Create/edit flows happen inline in the detail area
- Delete confirmation remains as an `AlertDialog` overlay

## Dependencies and Order
1. `use-automations-nav.ts` first — other components depend on it
2. `AutomationsNavPanel.vue` second — needs the nav composable
3. `AutomationDetailPanel.vue` third — needs the nav composable
4. `AutomationsPage.vue` refactor fourth — wires detail panel, removes modals
5. `ContextPanel.vue` update fifth — wires nav panel
6. `AGENTS.md` update last — documentation

## Tasks

- [x] 1. Create `use-automations-nav.ts`
  - **What**: Module-level shared composable following `use-settings-nav.ts` pattern. State: `activeAutomationId: Ref<string | null>` and `viewMode: Ref<'list' | 'create' | 'edit'>`. Expose `setActiveAutomation(id: string)`, `startCreate()`, `clearSelection()`. When `setActiveAutomation` is called, set `viewMode` to `'edit'`; when `startCreate`, set id to null and mode to `'create'`; `clearSelection` resets both.
  - **Files**: `client/src/composables/use-automations-nav.ts`
  - **Depends on**: None
  - **Acceptance**:
    - Exports `useAutomationsNav()` returning `activeAutomationId`, `viewMode`, `setActiveAutomation`, `startCreate`, `clearSelection`
    - State is module-level (shared across components like `use-settings-nav.ts`)

- [x] 2. Create `AutomationsNavPanel.vue`
  - **What**: Left nav panel listing all automations by name, with an active state highlight. Include a "New Automation" button at the top (like the `+ New` pattern). Each item shows automation name and an enabled/disabled dot indicator. Uses `useAutomations()` to get the list and `useAutomationsNav()` for selection state. Follows `SettingsNavPanel.vue` styling (`.settings-nav-panel` class pattern, but renamed to `.automations-nav-panel`).
  - **Files**: `client/src/components/automations/AutomationsNavPanel.vue`
  - **Depends on**: Task 1
  - **Acceptance**:
    - Renders automation names as clickable nav items
    - Active item has visual highlight matching Settings pattern
    - "New Automation" button calls `startCreate()` from nav composable
    - Shows enabled/disabled status dot per item
    - Loading and empty states handled

- [x] 3. Create `AutomationDetailPanel.vue`
  - **What**: Detail view component that renders based on `viewMode` from `useAutomationsNav()`. When `viewMode === 'list'` (nothing selected): show empty state prompt ("Select an automation or create a new one"). When `viewMode === 'create'`: render `AutomationForm` in create mode with submit/cancel handlers. When `viewMode === 'edit'`: find the automation by `activeAutomationId` from `useAutomations()`, show automation details (reuse content from `AutomationCard.vue` but in a full-width detail layout) with Edit and Delete action buttons. Clicking Edit switches to showing `AutomationForm` in edit mode inline. Include the play/pause/enable/disable actions. Keep `AlertDialog` for delete confirmation.
  - **Files**: `client/src/components/automations/AutomationDetailPanel.vue`
  - **Depends on**: Tasks 1, 2
  - **Acceptance**:
    - Empty state when nothing selected
    - Create mode shows `AutomationForm` with mode="create"
    - Edit view shows automation details with action buttons
    - Edit button transitions to inline `AutomationForm` with mode="edit"
    - Successful create/edit calls `setActiveAutomation(id)` to show the result
    - Delete confirmation uses `AlertDialog`, on success calls `clearSelection()`

- [x] 4. Refactor `AutomationsPage.vue`
  - **What**: Strip out all modal dialog code, card grid, and loading/empty states. Replace with a simple wrapper that renders `AutomationDetailPanel`. The page becomes minimal — just a container for the detail panel (similar to how `SettingsPage.vue` conditionally renders sections). Remove Dialog/AlertDialog imports. Remove local state for `isDialogOpen`, `dialogMode`, `editingAutomation`, `isSubmitting`, `submitError`, `deleteConfirmOpen`, `deletingAutomationId`, `isDeleting` — all of this moves to `AutomationDetailPanel.vue`.
  - **Files**: `client/src/components/pages/AutomationsPage.vue`
  - **Depends on**: Task 3
  - **Acceptance**:
    - No modal dialogs remain in this file
    - Renders `AutomationDetailPanel` as the main content
    - File is significantly reduced (target ~20-30 lines)

- [x] 5. Wire `ContextPanel.vue` for automations
  - **What**: Replace the `automations: SessionsPanel` mapping with an `AutomationsContextPanel` inline component (following the `SettingsContextPanel` pattern). Import `AutomationsNavPanel` and `useAutomationsNav`. The inline component renders `AutomationsNavPanel` and wires selection through the composable (though since nav composable is module-level, the panel can use it directly — no need for `modelValue` prop pattern unless preferred for consistency). Decide: for consistency with Settings, use `modelValue`/`update:modelValue` on the nav panel to emit the selected id, and have the context panel bridge it to the composable.
  - **Files**: `client/src/components/layout/ContextPanel.vue`
  - **Depends on**: Task 2
  - **Acceptance**:
    - `automations` rail renders `AutomationsNavPanel` instead of `SessionsPanel`
    - `AutomationsContextPanel` defined inline like `SettingsContextPanel`
    - Imports added for `AutomationsNavPanel` and `useAutomationsNav`

- [x] 6. Update `AGENTS.md` layout table
  - **What**: Change the Automations route composition from `[automations-list]` to `[automations-list][automations-detail]`. Add `automations-list` and `automations-detail` to the Panel Definitions table.
  - **Files**: `AGENTS.md`
  - **Depends on**: Task 5
  - **Acceptance**:
    - Route table shows `[automations-list][automations-detail]`
    - Panel definitions include both new panels

- [x] 7. Verify the refactor
  - **What**: Run `bunx vue-tsc --noEmit` from `client/` to verify no type errors. Manually verify in browser: navigate to Automations rail, see nav panel on left with automation list, click an automation to see detail, create new automation inline, edit inline, delete with confirmation. Verify Settings page still works unchanged.
  - **Depends on**: Tasks 1-6
  - **Acceptance**:
    - `bunx vue-tsc --noEmit` passes
    - Automations nav panel renders in context area
    - Create/edit/delete flows work inline without modals
    - Settings page unaffected

## Verification
```bash
cd client
bunx vue-tsc --noEmit
bun run build
```
Both commands should pass with zero errors. Manual browser testing confirms the new layout works for all CRUD operations.
