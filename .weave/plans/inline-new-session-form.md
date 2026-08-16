# Inline New Session Form

## TL;DR
Replace the modal `NewSessionDialog.vue` with a route-based inline form at `/sessions/new` rendered in the conversation panel area.

## Context
- `NewSessionDialog.vue` (1131 lines) is a full modal dialog with form logic for creating sessions
- It's opened via `useWorkspaceUiStore.openNewSessionDialog()` from multiple trigger points
- Routes use TanStack Vue Router file-based routing (e.g., `sessions.$id.tsx`)
- The conversation panel area is where `sessions.$id.tsx` renders; the new form takes its place
- Query params can carry `projectId` and GitHub source preset data (replacing dialog props)

## Scope
- In scope: New route file, new inline form component, updating all trigger points, removing modal dialog, updating store
- Out of scope: Changing form fields/validation logic, modifying the layout shell, changing session creation API
- Constraints / assumptions: Vue 3 Composition API + `<script setup>`, shadcn-vue, TanStack Vue Router, bun

## Objectives
- Render new session form inline in conversation panel via `/sessions/new` route
- Preserve all existing form logic (source selection, isolation strategy, harness, validation)
- Clean centered layout with toggle button groups and spacious sections
- Remove modal dialog and all associated open/close state

## Dependencies and Order
1. Create route + form component first (can coexist with dialog)
2. Update trigger points to navigate instead of opening dialog
3. Remove dialog component and store state last

## Tasks

- [ ] 1. Create route file `sessions.new.tsx`
  - **What**: Create a TanStack Vue Router file route at `/sessions/new` that renders the new form component. Accept optional search params (`projectId`, `source` as JSON-encoded GitHub preset).
  - **Files**: `client/src/routes/sessions.new.tsx`
  - **Depends on**: None
  - **Acceptance**:
    - Route exists and renders in the conversation panel area
    - Search params are parsed and passed as props to the form component

- [ ] 2. Create `NewSessionForm.vue` inline form component
  - **What**: Extract all form logic from `NewSessionDialog.vue` into a new component. Remove Dialog wrapper. Render as a centered, scrollable page with max-width constraint (~640px). Use toggle button groups (shadcn `ToggleGroup`) for Source Kind and Isolation Strategy. Keep all fields: source kind, repository picker, directory picker, isolation strategy, worktree options, harness selection, project selection, initial prompt. Bottom action bar with Cancel (navigates back) and Create Session buttons.
  - **Files**: `client/src/components/sessions/NewSessionForm.vue`
  - **Depends on**: Task 1
  - **Acceptance**:
    - All fields from `NewSessionDialog.vue` are present
    - Validation logic preserved (can't submit without required fields)
    - On successful creation, navigates to `/sessions/{id}`
    - Cancel navigates back to previous route or `/sessions`
    - Centered layout, clean spacing, toggle groups for choices
    - Accepts `projectId` and `initialSource` as props

- [ ] 3. Update `SessionsPanel.vue` triggers
  - **What**: Replace calls to `workspaceUiStore.openNewSessionDialog(projectId)` with `router.navigate({ to: '/sessions/new', search: { projectId } })`. Remove `NewSessionDialog` import and template usage.
  - **Files**: `client/src/components/sessions/SessionsPanel.vue`
  - **Depends on**: Task 2
  - **Acceptance**:
    - "New Session" button navigates to `/sessions/new`
    - No more `NewSessionDialog` reference in this file

- [ ] 4. Update `FleetDashboard.vue` trigger
  - **What**: Replace `workspaceUiStore.openNewSessionDialog(null)` with navigation to `/sessions/new`.
  - **Files**: `client/src/components/dashboard/FleetDashboard.vue`
  - **Depends on**: Task 2
  - **Acceptance**:
    - Dashboard "New Session" button navigates to route

- [ ] 5. Update `GitHubWorkItemDetailPage.vue` trigger
  - **What**: Replace `openNewSessionDialog(null, preset)` with navigation to `/sessions/new?source=<encoded-preset>`.
  - **Files**: `client/src/components/pages/GitHubWorkItemDetailPage.vue`
  - **Depends on**: Task 2
  - **Acceptance**:
    - GitHub source preset is encoded in search params and decoded by the form

- [ ] 6. Update `use-commands.ts` trigger
  - **What**: Replace `workspaceUiStore.openNewSessionDialog(null)` with router navigation.
  - **Files**: `client/src/composables/use-commands.ts`
  - **Depends on**: Task 2
  - **Acceptance**:
    - Command palette "New Session" command navigates to route

- [ ] 7. Remove dialog state from `workspace-ui.ts`
  - **What**: Remove `newSessionDialogOpen`, `newSessionDialogProjectId`, `newSessionDialogInitialSource`, `openNewSessionDialog`, `closeNewSessionDialog`, `setNewSessionDialogOpen` from the store. Optionally add a `navigateToNewSession(router, projectId?, source?)` helper or leave navigation inline at call sites.
  - **Files**: `client/src/stores/workspace-ui.ts`
  - **Depends on**: Tasks 3–6
  - **Acceptance**:
    - No dialog-related state remains in the store
    - Store tests updated accordingly

- [ ] 8. Delete `NewSessionDialog.vue`
  - **What**: Remove the file entirely.
  - **Files**: `client/src/components/sessions/NewSessionDialog.vue`
  - **Depends on**: Tasks 3–7
  - **Acceptance**:
    - File deleted
    - No remaining imports of `NewSessionDialog` anywhere in codebase

- [ ] 9. Update tests
  - **What**: Update `client/src/stores/__tests__/workspace-ui.test.ts` to remove dialog state tests. Update `client/src/components/__tests__/GitHubWorkItemDetailPage.test.ts` to assert navigation instead of `openNewSessionDialog` spy. Add basic test for `NewSessionForm.vue` if feasible.
  - **Files**: `client/src/stores/__tests__/workspace-ui.test.ts`, `client/src/components/__tests__/GitHubWorkItemDetailPage.test.ts`
  - **Depends on**: Tasks 7–8
  - **Acceptance**:
    - All existing tests pass with modifications
    - No references to removed store methods in tests

## Verification
```bash
cd client
bun run build        # no type errors, no missing imports
bun run test         # all unit tests pass
bunx vue-tsc --noEmit  # type check
```
Navigate to `/sessions/new` in browser — form renders inline. Create a session — navigates to session. Cancel — navigates back.
