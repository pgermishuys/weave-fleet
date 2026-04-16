# Project Context Menu: New Session

## TL;DR
> **Summary**: Add a "New Session" item to the project sidebar context menu that opens NewSessionDialog pre-selecting that project, plus E2E test coverage.
> **Estimated Effort**: Quick

## Context
### Original Request
Add "New Session" to the project sidebar context menu so users can create sessions scoped to a project without manually selecting it in the dialog.
### Key Findings
- `sidebar-project-item.tsx` already has a context menu (Rename, Move Up/Down, Delete)
- `NewSessionDialog` accepts optional props but lacks `initialProjectId`
- E2E page objects have session-level context menu helpers but no project-level ones

## Objectives
### Core Objective
Let users right-click a project → "New Session" → dialog opens with that project pre-selected.
### Deliverables
- [ ] "New Session" context menu item on project sidebar items
- [ ] `initialProjectId` prop on NewSessionDialog
- [ ] E2E test verifying the flow
### Definition of Done
- [ ] `dotnet test` passes for E2E session lifecycle tests
- [ ] Manual: right-click project → New Session → dialog shows correct project pre-selected
### Guardrails (Must NOT)
- Do not change existing context menu item behavior
- Do not alter NewSessionDialog's default behavior when `initialProjectId` is not provided

## TODOs

- [x] 1. Add `initialProjectId` prop to NewSessionDialog
  **What**: Add optional `initialProjectId?: string` prop. In the component, initialize `selectedProjectId` state from `initialProjectId ?? ""` instead of `""`.
  **Files**: `client/src/components/session/new-session-dialog.tsx`
  **Acceptance**: Dialog pre-selects project when `initialProjectId` is passed; behaves unchanged without it.

- [x] 2. Add "New Session" context menu item and `data-project-id` attribute
  **What**: Add `data-project-id={group.projectId}` to the project header div. Add a "New Session" `ContextMenuItem` with `Plus` icon as the first menu item. Wrap it with `NewSessionDialog` passing `initialProjectId={group.projectId}`. Manage dialog open state locally.
  **Files**: `client/src/components/layout/sidebar-project-item.tsx`
  **Acceptance**: Context menu shows "New Session" at top; clicking it opens dialog with project pre-selected.

- [x] 3. Add project context menu helpers to FleetSidebarPage
  **What**: Add `GetProjectItem(string projectId)` (locates by `data-project-id`), `OpenProjectContextMenuAsync(string projectId)`, and `ClickProjectMenuItemAsync(string projectId, string menuItem)` methods.
  **Files**: `tests/WeaveFleet.E2E/Pages/FleetSidebarPage.cs`
  **Acceptance**: Methods compile and can target project items by ID.

- [x] 4. Add E2E test for New Session from project context menu
  **What**: Add test that creates a project, right-clicks it, clicks "New Session", verifies dialog opens with project pre-selected, creates a session, and verifies it appears under that project.
  **Files**: `tests/WeaveFleet.E2E/Tests/SessionLifecycleTests.cs`
  **Acceptance**: Test passes in CI.

## Verification
- [x] `npm run build` succeeds (frontend)
- [x] `dotnet build` succeeds (E2E project)
- [ ] `dotnet test --filter "NewSessionFromProjectContextMenu"` passes
- [ ] No regressions in existing session lifecycle tests
