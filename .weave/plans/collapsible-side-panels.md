# Collapsible Side Panels with TODO Visibility

## TL;DR
> **Summary**: Make both left (sessions) and right (detail) panels collapsible with a collapsed-right rail showing TODO progress, localStorage persistence, and smart auto-open triggers.
> **Estimated Effort**: Medium

## Context
### Original Request
Both side panels should collapse/expand. The TODO list (rendered in the right panel via `TodoListView.vue`) must remain discoverable when the right panel is collapsed.

### Key Findings
- **Left panel** already collapses via `useSidebarStore().panelCollapsed` — `ContextPanel` is conditionally rendered with `v-if="!panelCollapsed"` in `AppShell.vue`.
- **Right panel** (`RightPanel.vue`) is always rendered at fixed 280px width. No collapse state exists.
- **Sidebar store** (`stores/sidebar.ts`) owns `panelCollapsed` for the left panel. Pattern: `shallowRef` + setter + toggle.
- **Layout** is a simple flex row in `AppShell.vue`: `IconRail | ContextPanel? | CenterContent | RightPanel`.
- **TODO data** flows: `SessionDetailPanel.vue` calls `extractLatestTodos()` from `lib/todo-utils` and passes results to `TodoListView.vue`. Todos are per-session, derived from session events.
- **No localStorage persistence** exists for any panel state today.

## Objectives
### Core Objective
Let users collapse either side panel to maximize the center workspace, while keeping TODO status always glanceable via a collapsed rail.

### Deliverables
- [ ] Right panel collapse/expand with animated transition
- [ ] Collapsed-right rail showing TODO count/progress badge
- [ ] Left panel collapse gets localStorage persistence
- [ ] Right panel collapse gets localStorage persistence
- [ ] Auto-open right panel on first session select and new TODO arrival
- [ ] Keyboard shortcuts for toggling each panel

### Definition of Done
- [ ] Both panels collapse and expand without layout jank
- [ ] Collapsed right rail shows accurate TODO count and progress ring/bar
- [ ] Clicking the rail or pressing shortcut expands the right panel
- [ ] Panel states survive page reload (localStorage)
- [ ] Auto-open fires on session select (when no session was selected) and when TODO count increases
- [ ] All existing tests pass: `pnpm test`

### Guardrails (Must NOT)
- Never auto-close either panel
- Don't remove or relocate the full TODO list from the right panel
- Don't break existing left-panel collapse behavior

## TODOs

- [ ] 1. **Extend sidebar store with right panel state**
  **What**: Add `rightPanelCollapsed` shallowRef, setter, and toggle to `useSidebarStore`. Add localStorage read/write for both `panelCollapsed` and `rightPanelCollapsed` (keys: `weave:left-collapsed`, `weave:right-collapsed`). Initialize from localStorage on store creation.
  **Files**: `client/src/stores/sidebar.ts`, `client/src/stores/__tests__/sidebar.test.ts`
  **Acceptance**: Store exposes `rightPanelCollapsed`, `setRightPanelCollapsed`, `toggleRightPanelCollapsed`. Values persist across page reloads.

- [ ] 2. **Create CollapsedRightRail component**
  **What**: A narrow (~40px) vertical rail shown when right panel is collapsed. Contains: (a) a clickable expand button/chevron at top, (b) a circular TODO progress indicator (ring or mini progress bar) showing `completedCount/totalCount`, (c) a numeric badge for pending TODO count. Clicking anywhere on the rail calls `toggleRightPanelCollapsed()`. The rail receives `todos` as a prop (same `TodoEntry[]` type from `lib/todo-utils`).
  **Files**: `client/src/components/layout/CollapsedRightRail.vue`
  **Acceptance**: Rail renders with correct TODO stats; click expands right panel.

- [ ] 3. **Wire right panel collapse into AppShell layout**
  **What**: In `AppShell.vue`, conditionally render `RightPanel` vs `CollapsedRightRail` based on `rightPanelCollapsed`. Pass TODO data to the rail. Add CSS transition on the swap (width animation or fade). Ensure `CenterContent` flex-grows to fill freed space.
  **Files**: `client/src/components/layout/AppShell.vue`
  **Acceptance**: Toggling `rightPanelCollapsed` swaps between full panel and rail; center content resizes smoothly.

- [ ] 4. **Add collapse toggle button to RightPanel header**
  **What**: Add a collapse chevron/button to `RightPanelTabs.vue` (or the top of `RightPanel.vue`) that calls `toggleRightPanelCollapsed()`. Mirror the pattern used for left panel collapse if one exists in `IconRail` or `ContextPanel`.
  **Files**: `client/src/components/layout/RightPanel.vue` or `client/src/components/layout/RightPanelTabs.vue`
  **Acceptance**: Button visible in right panel header; clicking it collapses the panel.

- [ ] 5. **Persist left panel collapsed state to localStorage**
  **What**: The left panel's `panelCollapsed` already works but isn't persisted. Add localStorage sync in the sidebar store (same mechanism as step 1).
  **Files**: `client/src/stores/sidebar.ts`
  **Acceptance**: Left panel remembers collapsed state across reloads.

- [ ] 6. **Auto-open right panel on session select and new TODOs**
  **What**: Add a watcher (in `AppShell.vue` or a composable) that: (a) opens right panel when `activeSessionId` transitions from `null` to a value (first select), (b) opens right panel when the active session's TODO count increases. Use `watch` with `{ flush: 'post' }`. Never auto-close.
  **Files**: `client/src/components/layout/AppShell.vue` (or new `client/src/composables/use-right-panel-auto-open.ts`)
  **Acceptance**: Selecting a session when none was selected opens the right panel. A new TODO appearing opens the right panel if collapsed.

- [ ] 7. **Keyboard shortcuts**
  **What**: Register shortcuts in the keybindings store or via `useCommands`: `Cmd+B` / `Ctrl+B` for left panel toggle (if not already), `Cmd+Shift+B` / `Ctrl+Shift+B` for right panel toggle.
  **Files**: `client/src/stores/keybindings.ts` (or wherever shortcuts are registered), `client/src/composables/use-commands.ts`
  **Acceptance**: Shortcuts toggle respective panels.

- [ ] 8. **Plumb TODO data to AppShell for the collapsed rail**
  **What**: The TODO data currently lives deep in `SessionDetailPanel.vue`. Either: (a) lift `extractLatestTodos` into a composable (`use-session-todos.ts`) that reads from session events and is callable from `AppShell.vue`, or (b) expose a computed from the sessions store. Option (a) preferred for reuse.
  **Files**: `client/src/composables/use-session-todos.ts` (new), `client/src/components/session/SessionDetailPanel.vue` (refactor to use the composable)
  **Acceptance**: `AppShell.vue` can access current session's TODO list without duplicating extraction logic.

## Verification
- [ ] All tests pass: `pnpm test` in `client/`
- [ ] No regressions: left panel collapse still works identically
- [ ] Both panels collapse/expand with no layout shift or overflow
- [ ] localStorage keys written and read correctly (check in DevTools)
- [ ] Auto-open triggers fire correctly (manual QA)
- [ ] Collapsed rail shows accurate TODO progress
- [ ] Keyboard shortcuts work on both macOS and Windows/Linux

## Risks & Notes
- **TODO data plumbing (step 8)**: Currently `extractLatestTodos` is called inside `SessionDetailPanel` using session event data fetched via API. The composable needs access to the same event stream. Check if `useSessionEvents` is reusable or if events are already in a store.
- **Animation performance**: Width transitions on flex children can cause reflow. Consider using `transform: translateX()` or `width` with `will-change` for the right panel transition.
- **Recommended implementation order**: 1 → 5 → 8 → 2 → 3 → 4 → 6 → 7 (store first, then data plumbing, then UI, then polish).
