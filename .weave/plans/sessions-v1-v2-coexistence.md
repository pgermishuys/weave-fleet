# Sessions V1/V2 Coexistence

## TL;DR
> **Summary**: Port the predecessor's workspace-grouped session UI (V1) into the current app alongside the project-based UI (V2), behind a feature toggle, sharing the same sessions store and API.
> **Estimated Effort**: Large

## Context
### Original Request
Bring the predecessor (weave-agent-fleet) workspace-based session UX into the current Vue 3 app as "SessionsV1", coexisting with the current project-based "SessionsV2" via feature toggle.

### Key Findings

**Current app (V2) architecture:**
- `SidebarRail` type in `stores/sidebar.ts` controls which left panel shows — currently: `board | sessions | analytics | github | marketplace | settings`
- `ContextPanel.vue` maps `activeRail` → panel component. `"sessions"` → `SessionsPanel.vue`
- `IconRail.vue` defines rail icons — `topItems` array has board + sessions, `bottomItems` has plugins/analytics/settings
- Route `/` renders `FleetDashboard.vue` (card grid of all sessions) — will become `/sessions-v2`
- `SessionsPanel.vue` (left panel) groups by projects, uses `useProjects()` composable + drag-drop
- Shared infra: `useSessionsStore` (Pinia), `useSessions()` composable, `useWorkspaces()` composable, `SessionListItem` type, `/api/sessions` API

**Predecessor (V1) components to port (React → Vue):**
| Predecessor (React) | Purpose | Vue equivalent needed |
|---|---|---|
| `fleet-panel.tsx` | Left sidebar — workspace tree with hide-inactive, delete-inactive, keyboard nav | `SessionsV1Panel.vue` |
| `session-group.tsx` | Collapsible workspace group — rename, new session, terminate all | `WorkspaceGroup.vue` |
| `live-session-card.tsx` | Session card with status dot, badges, hover actions (terminate/resume/delete/abort), context menu | `WorkspaceSessionItem.vue` |
| `fleet-toolbar.tsx` | Search + group-by/sort-by dropdowns with localStorage persistence | `WorkspaceToolbar.vue` |
| `summary-bar.tsx` | Stats bar (active/idle/tokens/queued) | Already exists as `SummaryBar.vue` in V2 dashboard — reusable |
| `confirm-delete-session-dialog.tsx` | Delete confirmation | Already exists as `ConfirmDeleteSessionDialog.vue` |
| `session-card.tsx` | Static dashboard card (not live) | Already exists as `SessionCard.vue` in V2 |

**Predecessor composables already ported:**
- `useWorkspaces()` — already in `client/src/composables/use-workspaces.ts`, wraps `groupSessionsByWorkspace()`
- `useRenameWorkspace()` — already in `client/src/composables/use-rename-workspace.ts`
- `usePersistedState` — already in `client/src/composables/use-persisted-state.ts` (used by V2)

**Not yet ported / V1-specific needs:**
- Workspace tree keyboard navigation (ArrowUp/Down/Left/Right/Enter/F2)
- Hide-inactive toggle + bulk-delete-inactive
- Group-by options beyond directory (session-status, connection-status, source, none)
- Sort-by options (recent, name, status)
- Session nesting (parent/child via `nestSessions()`)
- Resume session action
- Abort (interrupt) session action
- Context menu with "Open in" tool submenu

## Objectives
### Core Objective
Enable both workspace-grouped (V1) and project-grouped (V2) session views to coexist in the app, switchable via feature toggle.

### Deliverables
- [ ] Feature toggle mechanism (localStorage + optional config flag)
- [ ] V1 sidebar panel (`SessionsV1Panel.vue`) with workspace tree
- [ ] V1 workspace group component with collapsible sections
- [ ] V1 session card with status, hover actions, context menu
- [ ] V1 toolbar with search/group-by/sort-by
- [ ] V1 dashboard route (center content, card grid grouped by workspace)
- [ ] Icon rail integration — V1 gets its own rail entry or replaces V2's
- [ ] Shared store/composable consumption — no duplication

### Definition of Done
- [ ] Toggle to V1: sidebar shows workspace tree, `/sessions-v1` route shows workspace-grouped dashboard
- [ ] Toggle to V2: sidebar shows project tree, `/sessions-v2` route shows project-grouped dashboard (existing behavior)
- [ ] Both can be active simultaneously (two sidebar rails)
- [ ] Creating/deleting a session in either view reflects in the other
- [ ] `npm run build` succeeds with no type errors
- [ ] Existing V2 tests still pass

### Guardrails (Must NOT)
- Must NOT modify the sessions API or backend
- Must NOT change `SessionListItem` type or `useSessionsStore`
- Must NOT break V2 functionality when V1 is disabled
- Must NOT duplicate session fetching logic — both views consume `useSessions()`
- Must NOT port the predecessor's `session-card.tsx` (static dashboard card) — V2's `SessionCard.vue` already covers this

## TODOs

- [x] 1. **Feature Toggle Infrastructure**
  **What**: Create a `useSessionsViewMode()` composable that reads/writes a `weave:sessions-view-mode` localStorage key. Values: `"v1"`, `"v2"`, `"both"`. Default `"v2"`. Expose `viewMode`, `isV1Enabled`, `isV2Enabled`, `setViewMode()`. Also check for an optional server config flag from `/api/config` to allow admin override.
  **Files**: `client/src/composables/use-sessions-view-mode.ts`
  **Acceptance**: Import composable, call `setViewMode("v1")` — `isV1Enabled` returns true, `isV2Enabled` returns false.

- [x] 2. **Add `"sessions-v1"` to SidebarRail Type**
  **What**: Add `"sessions-v1"` to the `SidebarRail` union in `stores/sidebar.ts`. This is V1's rail identity. Update `isSidebarRail()` in `IconRail.vue` to include it.
  **Files**: `client/src/stores/sidebar.ts`, `client/src/components/layout/IconRail.vue`
  **Acceptance**: TypeScript compiles. `setActiveRail("sessions-v1")` works without type error.

- [x] 3. **Icon Rail — Conditional V1/V2 Items**
  **What**: In `IconRail.vue`, make the top `sessions` rail item conditional on V2 being enabled. Add a `sessions-v1` rail item (icon: `Rows3` or `FolderTree` from lucide) conditional on V1 being enabled. When both enabled, both icons show. Update `currentRouteRail` computed to map `/sessions-v1` → `"sessions-v1"`.
  **Files**: `client/src/components/layout/IconRail.vue`
  **Acceptance**: With mode `"both"`, two rail icons appear (Sessions V2 + Sessions V1). Clicking Sessions V1 sets `activeRail` to `"sessions-v1"` and navigates to `/sessions-v1`.

- [x] 4. **Context Panel — Wire Up Fleet Panel**
  **What**: In `ContextPanel.vue`, add `"sessions-v1": SessionsV1Panel` to `panelComponents`. Import `SessionsV1Panel.vue` (created in TODO 6).
  **Files**: `client/src/components/layout/ContextPanel.vue`
  **Acceptance**: When `activeRail` is `"sessions-v1"`, the left panel renders `SessionsV1Panel`.

- [x] 5. **V1 Toolbar Component**
  **What**: Port `fleet-toolbar.tsx` → `WorkspaceToolbar.vue`. Vue 3 Composition API. Props: `groupBy`, `sortBy`, `search` as v-models. Persist prefs to localStorage under `weave:fleet:prefs`. Use existing shadcn-vue `DropdownMenu`, `Button`, `Input` components.
  **Files**: `client/src/components/sessions-v1/WorkspaceToolbar.vue`
  **Acceptance**: Component renders search input + two dropdown buttons. Changing group-by persists to localStorage.

- [x] 6. **V1 Left Sidebar — WorkspacesPanel.vue**
  **What**: Port `fleet-panel.tsx` → `SessionsV1Panel.vue`. Shows: header row with "Sessions V1" label + hide-inactive toggle + delete-inactive button + new-session button, then a workspace tree. Uses `useWorkspaces()` composable (already exists), `useSessions()` for data. Supports keyboard nav (ArrowUp/Down/Left/Right). Each workspace renders `WorkspaceGroup.vue` (TODO 7).
  **Files**: `client/src/components/sessions-v1/SessionsV1Panel.vue`
  **Acceptance**: Panel renders workspace groups from session data. Hide-inactive toggle filters stopped/completed sessions. Keyboard nav works.

- [x] 7. **V1 Workspace Group Component**
  **What**: Port `session-group.tsx` → `WorkspaceGroup.vue`. Collapsible group header with: chevron, status dot, inline-editable display name, session count badge, overflow menu (new session, open in tool, terminate all). Collapse state persisted to `weave:fleet:collapsed`. Renders `WorkspaceSessionItem.vue` for each session, with parent/child nesting via `nestSessions()`.
  **Files**: `client/src/components/sessions-v1/WorkspaceGroup.vue`
  **Acceptance**: Groups expand/collapse. Inline rename calls `useRenameWorkspace()`. Overflow menu actions work.

- [x] 8. **V1 Session Item Component**
  **What**: Port `live-session-card.tsx` → `WorkspaceSessionItem.vue`. Compact list item (not full card) for sidebar use. Status dot, title, badges (working/idle, worktree/copy, conductor/child, disconnected/stopped), relative time, token count. Hover actions: terminate, abort, resume, delete. Context menu with "Open in" submenu.
  **Files**: `client/src/components/sessions-v1/WorkspaceSessionItem.vue`
  **Acceptance**: Items render with correct status. Hover reveals action buttons. Context menu works.

- [x] 9. **V1 Dashboard Route**
  **What**: Create `/sessions-v1` route that renders `SessionsV1Dashboard.vue` — a center-content view showing the workspace-grouped card grid. Reuses `SessionCard.vue` from V2 dashboard. Includes `WorkspaceToolbar.vue` for search/group/sort. Groups sessions using `useWorkspaces()` with toolbar controls applied.
  **Files**: `client/src/routes/sessions-v1.tsx`, `client/src/components/sessions-v1/SessionsV1Dashboard.vue`
  **Acceptance**: Navigate to `/sessions-v1` — see sessions grouped by workspace directory in card grid. Search filters. Group-by/sort-by controls work.

- [x] 10. **Session Nesting Utility**
  **What**: Ensure `nestSessions()` is available in the Vue codebase. Check if `client/src/lib/session-utils.ts` already exports it (it's used in predecessor's `session-group.tsx`). If not, port from predecessor's `lib/session-utils.ts`. The function groups sessions by `parentSessionId` into parent/child trees.
  **Files**: `client/src/lib/session-utils.ts`
  **Acceptance**: `nestSessions(items)` returns `Array<{ item, children }>` with children nested under their parent.

- [x] 11. **V1-Specific Session Actions Composable**
  **What**: Ensure resume/abort/terminate actions are available. Check existing `use-session-actions.ts`. If missing, add `useResumeSession()`, `useAbortSession()`, `useTerminateSession()` composables that call the appropriate API endpoints.
  **Files**: `client/src/composables/use-session-actions.ts`
  **Acceptance**: Calling `resumeSession(id)` sends POST to resume endpoint. Same for abort/terminate.

- [x] 12. **Settings UI — View Mode Toggle**
  **What**: Add a "Sessions View" toggle to the Settings panel. Radio group or segmented control: V1 (Workspaces) / V2 (Projects) / Both. Uses `useSessionsViewMode()` composable.
  **Files**: `client/src/components/settings/SessionsViewSetting.vue`, integrate into existing settings page
  **Acceptance**: Changing the toggle immediately switches which rail icons and panels are visible.

- [x] 13. **Route Guard — Redirect on Disabled View**
  **What**: If V1 is disabled and user navigates to `/sessions-v1`, redirect to `/sessions-v2`. If V2 is disabled and user navigates to `/sessions-v2`, redirect to `/sessions-v1`. Add `beforeLoad` guard on both routes using `useSessionsViewMode()`.
  **Files**: `client/src/routes/index.tsx`, `client/src/routes/sessions-v1.tsx`
  **Acceptance**: With mode `"v2"`, navigating to `/sessions-v1` redirects to `/sessions-v2`. With mode `"v1"`, navigating to `/sessions-v2` redirects to `/sessions-v1`.

## Verification
- [x] All existing tests pass (`npm test`)
- [x] No regressions — V2 works identically when toggle is `"v2"`
- [x] `npm run build` succeeds (no TS errors)
- [ ] Toggle to V1: rail icon appears, sidebar shows workspace tree, `/sessions-v1` shows dashboard
- [ ] Toggle to V2: existing behavior unchanged
- [ ] Toggle to Both: two rail icons, both panels accessible, both routes work
- [ ] Create session in V1 view → appears in V2 view (and vice versa)
- [ ] No duplicate API polling — both views share single `useSessions()` instance via store

## Implementation Phasing

### Phase 1 — Foundation (TODOs 1, 2, 10, 11)
Feature toggle, sidebar type, session utilities. No UI yet.

### Phase 2 — Sidebar Panel (TODOs 5, 6, 7, 8)
V1 left panel with workspace tree. Can test by manually setting `activeRail` to `"sessions-v1"`.

### Phase 3 — Integration (TODOs 3, 4, 9)
Wire into icon rail, context panel, and dashboard route. V1 is now navigable.

### Phase 4 — Polish (TODOs 12, 13)
Settings toggle UI, route guards. Feature is user-facing and toggleable.

## File Tree Summary

```
client/src/
├── composables/
│   └── use-sessions-view-mode.ts          ← NEW (TODO 1)
├── components/
│   └── sessions-v1/                       ← NEW directory
│       ├── SessionsV1Dashboard.vue        ← NEW (TODO 9)
│       ├── SessionsV1Panel.vue            ← NEW (TODO 6)
│       ├── WorkspaceGroup.vue             ← NEW (TODO 7)
│       ├── WorkspaceSessionItem.vue       ← NEW (TODO 8)
│       └── WorkspaceToolbar.vue           ← NEW (TODO 5)
│   └── settings/
│       └── SessionsViewSetting.vue        ← NEW (TODO 12)
├── routes/
│   └── sessions-v1.tsx                    ← NEW (TODO 9)
├── stores/
│   └── sidebar.ts                         ← MODIFY (TODO 2)
├── lib/
│   └── session-utils.ts                   ← MODIFY if needed (TODO 10)
└── components/layout/
    ├── IconRail.vue                       ← MODIFY (TODO 3)
    └── ContextPanel.vue                   ← MODIFY (TODO 4)
```
