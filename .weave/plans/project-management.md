# Project Management Feature

## TL;DR
> **Summary**: Full project CRUD UI in the Fleet Panel sidebar — create, rename, delete projects; assign sessions to projects via context menu or at creation time; project reordering. All backend endpoints and most client hooks already exist; the work is primarily UI wiring.
> **Estimated Effort**: Large

## Context

### Original Request
Build a complete "Project Management" feature in the Fleet Panel sidebar covering: (A) Project CRUD UI, (B) Assign session to project, (C) New Session dialog project picker, (D) Project reordering.

### Key Findings

**Backend — fully ready:**
- `POST /api/projects` — create project (name, description) ✅
- `PATCH /api/projects/{id}` — update project (name, description) ✅
- `DELETE /api/projects/{id}?mode=move_to_scratch|delete_sessions` — delete project ✅
- `PATCH /api/projects/{id}/reorder` — reorder project (position) ✅
- `PATCH /api/sessions/{id}/project` — move session to project (`MoveSessionRequest { ProjectId }`) ✅
- `GET /api/sessions?projectId=` — filter sessions by project ✅
- `POST /api/sessions` — create session; the backend `CreateSessionRequest` already has `ProjectId` property ✅

**Backend gap (1 item):**
- `CreateSessionApiRequest` (line 262 of `SessionEndpoints.cs`) does NOT include a `ProjectId` field. The internal `CreateSessionRequest` has it, but it's never passed from the API layer. Must add `string? ProjectId` to `CreateSessionApiRequest` and wire it through in the endpoint handler.

**Client hooks — all exist:**
- `use-create-project.ts` — `useCreateProject()` → `POST /api/projects` ✅
- `use-update-project.ts` — `useUpdateProject()` → `PATCH /api/projects/{id}` ✅
- `use-delete-project.ts` — `useDeleteProject(mode)` → `DELETE /api/projects/{id}?mode=` ✅
- `use-reorder-project.ts` — `useReorderProject()` → `PATCH /api/projects/{id}/reorder` ✅
- `use-projects.ts` — `useProjects()` → `GET /api/projects` (list + refetch) ✅
- `use-move-session.ts` — `useMoveSession()` → `PATCH /api/sessions/{id}/project` ✅
- `use-create-session.ts` — `useCreateSession()` → `POST /api/sessions` (needs `projectId` added) ⚠️

**Client type gap:**
- `CreateSessionRequest` in `api-types.ts` has no `projectId` field. Must add it.
- `CreateSessionOptions` in `use-create-session.ts` has no `projectId` field. Must add it.

**UI components available:**
- `ContextMenu` + `ContextMenuSub` / `ContextMenuSubTrigger` / `ContextMenuSubContent` — for nested "Move to Project" submenu ✅
- `AlertDialog` — for delete confirmation ✅
- `InlineEdit` — for inline rename (used by workspace and session items) ✅
- `Dialog` / `DialogContent` — for create project dialog ✅
- `Select` / `SelectTrigger` / `SelectContent` / `SelectItem` — for project picker dropdown ✅
- `Popover` — alternative for create project popover ✅

**Existing patterns:**
- `sidebar-workspace-item.tsx` uses `ContextMenu` with `InlineEdit` for rename, plus context menu items for pin/new session/terminate — exact pattern to replicate for project items.
- `sidebar-session-item.tsx` uses `ContextMenu` with `ContextMenuSeparator` and destructive variant for delete — exact pattern for "Move to Project" submenu.
- `confirm-delete-session-dialog.tsx` uses `AlertDialog` with loading state — exact pattern for "Delete Project" confirmation.
- `fleet-panel.tsx` renders `SidebarProjectItem` for each project group and a `NewSessionDialog` button — the "New Project" button goes here.
- `sidebar-project-item.tsx` currently is a simple collapsible with no context menu — needs context menu added.

## Objectives

### Core Objective
Enable users to organize sessions into projects with full lifecycle management (create, rename, delete, assign, reorder) directly from the sidebar UI.

### Deliverables
- [x] "New Project" button in the Fleet Panel header
- [x] Create Project dialog with name + optional description
- [x] Project context menu on `SidebarProjectItem` (Rename, Delete)
- [x] Inline rename for projects using `InlineEdit`
- [x] Delete Project confirmation dialog with mode selection
- [x] "Move to Project" context menu submenu on session items
- [x] Project picker dropdown in New Session dialog
- [x] Backend: wire `projectId` through `CreateSessionApiRequest`
- [x] Client: add `projectId` to `CreateSessionRequest` type and `useCreateSession` hook
- [x] Project reorder support (up/down buttons or drag)

### Definition of Done
- [x] `npx tsc --noEmit` passes with no new errors
- [x] All existing tests pass (`npx vitest run`)
- [x] Manual: can create a project from sidebar
- [x] Manual: can rename a project via context menu → inline edit
- [x] Manual: can delete a project (both modes) via context menu
- [x] Manual: can move a session to a different project via session context menu
- [x] Manual: new sessions can be assigned to a project at creation time
- [x] Manual: projects appear in correct order; reordering works

### Guardrails (Must NOT)
- Do NOT introduce drag-and-drop libraries (keep it simple with context menus)
- Do NOT modify the database schema (backend is already complete)
- Do NOT change the session grouping logic in `workspace-utils.ts` (it already handles `groupSessionsByProject` correctly)
- Do NOT remove or alter the "Ungrouped" bucket behavior (scratch projects are correctly merged into ungrouped)

## TODOs

### A) Backend: Wire `projectId` through session creation

- [x] 1. **Add `ProjectId` to `CreateSessionApiRequest`**
  **What**: Add `string? ProjectId` field to the `CreateSessionApiRequest` record and pass it through to `CreateSessionRequest` in the `POST /api/sessions` handler.
  **Files**:
    - `src/WeaveFleet.Api/Endpoints/SessionEndpoints.cs` — line 262: add `string? ProjectId` to the record; line 45-54: add `ProjectId = req.ProjectId` to the `CreateSessionRequest` init
  **Acceptance**: `dotnet build` succeeds; POST /api/sessions with `{ "directory": "...", "projectId": "..." }` assigns session to the specified project.

### B) Client types + hook: Add `projectId` to session creation

- [x] 2. **Add `projectId` to frontend `CreateSessionRequest`**
  **What**: Add `projectId?: string` to `CreateSessionRequest` in `api-types.ts`.
  **Files**: `client/src/lib/api-types.ts` — add `projectId?: string;` after line 48 (after `harnessType`)
  **Acceptance**: TypeScript compiles without errors.

- [x] 3. **Add `projectId` to `CreateSessionOptions` and `useCreateSession`**
  **What**: Thread `projectId` through the `CreateSessionOptions` interface and the `useCreateSession` hook's body construction.
  **Files**: `client/src/hooks/use-create-session.ts` — add `projectId?: string` to `CreateSessionOptions` (line 8-15) and include it in the `body` object (line 35-42).
  **Acceptance**: TypeScript compiles; `createSession("dir", { projectId: "p1" })` sends `projectId` in the request body.

### C) "New Project" button + Create Project dialog

- [x] 4. **Create `CreateProjectDialog` component**
  **What**: A dialog with a form containing: project name (required), description (optional), and a submit button. Uses `useCreateProject` hook. On success, calls `refetch` on the projects list. Follow the pattern of `NewSessionDialog` for dialog structure but much simpler (just 2 fields).
  **Files**: Create `client/src/components/fleet/create-project-dialog.tsx`
  **Details**:
    - Props: `trigger?: ReactNode`, `open?: boolean`, `onOpenChange?: (open: boolean) => void`, `onCreated?: () => void`
    - Uses `Dialog`, `DialogContent`, `DialogHeader`, `DialogTitle` from `@/components/ui/dialog`
    - Uses `Input` for name, `Textarea` (or `Input`) for description
    - Uses `Button` for submit
    - Uses `useCreateProject` hook
    - On success: calls `onCreated`, closes dialog
    - Shows error from hook if creation fails
  **Acceptance**: Component renders; entering name + clicking Create calls POST /api/projects.

- [x] 5. **Add "New Project" button to `FleetPanel`**
  **What**: Place a folder-plus icon button next to the existing "New Session" `+` button in the Fleet header row. Wire it to open the `CreateProjectDialog`. After project creation, refetch both projects and sessions.
  **Files**: `client/src/components/layout/fleet-panel.tsx`
  **Details**:
    - Import `CreateProjectDialog` and `FolderPlus` from lucide-react
    - Add a second `Tooltip`-wrapped button next to the New Session button (line ~119-136)
    - The `onCreated` callback should call `projects.refetch()` — but `useProjects` returns `refetch`. Need to destructure it: `const { projects, refetch: refetchProjects } = useProjects();`
    - Also call `refetch` (sessions) since project grouping may change
  **Acceptance**: Button visible in sidebar header; clicking it opens Create Project dialog; creating a project shows it in the sidebar.

### D) Project context menu (Rename + Delete)

- [x] 6. **Add context menu to `SidebarProjectItem`**
  **What**: Wrap the project header row in a `ContextMenu` component (same pattern as `SidebarWorkspaceItem`). Add "Rename" and "Delete" menu items. Only show context menu for named projects (not "Ungrouped").
  **Files**: `client/src/components/layout/sidebar-project-item.tsx`
  **Details**:
    - Import `ContextMenu`, `ContextMenuContent`, `ContextMenuItem`, `ContextMenuSeparator`, `ContextMenuTrigger` from `@/components/ui/context-menu`
    - Import `InlineEdit` from `@/components/ui/inline-edit`
    - Import `useUpdateProject` from `@/hooks/use-update-project`
    - Import `Pencil`, `Trash2` from lucide-react
    - Add `isRenaming` state
    - Wrap the existing `CollapsibleTrigger` div in `ContextMenuTrigger`
    - Replace the static project name `<span>` with `<InlineEdit>` for named projects
    - Context menu items:
      - "Rename" → sets `isRenaming(true)`
      - Separator
      - "Delete" (destructive) → opens delete confirmation dialog (see task 7)
    - For ungrouped projects, skip the context menu (no rename/delete for the ungrouped bucket)
    - The `handleRename` callback uses `useUpdateProject().updateProject(projectId, { name: newName })` then calls `refetch()`
  **Props change**: Add `refetchProjects?: () => void` to `SidebarProjectItemProps` to refresh the project list after rename/delete.
  **Acceptance**: Right-clicking a project shows Rename/Delete; rename via InlineEdit works; double-click rename works.

- [x] 7. **Create `ConfirmDeleteProjectDialog` component**
  **What**: An AlertDialog similar to `ConfirmDeleteSessionDialog` but with a mode selector (radio or two-button approach). Two options: "Move sessions to Ungrouped" (`move_to_scratch`) or "Delete all sessions" (`delete_sessions`).
  **Files**: Create `client/src/components/fleet/confirm-delete-project-dialog.tsx`
  **Details**:
    - Props: `open`, `onOpenChange`, `projectName: string`, `onConfirm: (mode: DeleteProjectMode) => void`, `isDeleting?: boolean`
    - Uses `AlertDialog` components (same pattern as `confirm-delete-session-dialog.tsx`)
    - Default mode: `move_to_scratch` (safer default)
    - Two radio buttons or a `Select` for mode choice
    - Description text explains what each mode does
    - Import `DeleteProjectMode` from `@/hooks/use-delete-project`
  **Acceptance**: Dialog renders with mode picker; confirming calls onConfirm with selected mode.

- [x] 8. **Wire delete into `SidebarProjectItem` context menu**
  **What**: Add state for `showDeleteConfirm`, wire the "Delete" context menu item to open the dialog, and handle confirmation via `useDeleteProject`.
  **Files**: `client/src/components/layout/sidebar-project-item.tsx`
  **Details**:
    - Import `useDeleteProject` from `@/hooks/use-delete-project`
    - Import `ConfirmDeleteProjectDialog`
    - Add `showDeleteConfirm` state
    - "Delete" menu item sets `showDeleteConfirm(true)`
    - On confirm: call `deleteProject(projectId, mode)`, then `refetch()` and `refetchProjects()`
    - Render `ConfirmDeleteProjectDialog` alongside the `Collapsible`
  **Acceptance**: Right-click → Delete → confirmation dialog → delete succeeds → project removed from sidebar.

### E) Move session to project (context menu)

- [x] 9. **Add "Move to Project" submenu to session context menu**
  **What**: Add a `ContextMenuSub` with a list of projects as submenu items in `SidebarSessionItem`. Selecting a project calls `useMoveSession`.
  **Files**: `client/src/components/layout/sidebar-session-item.tsx`
  **Details**:
    - Import `ContextMenuSub`, `ContextMenuSubTrigger`, `ContextMenuSubContent` from `@/components/ui/context-menu`
    - Import `useMoveSession` from `@/hooks/use-move-session`
    - Import `useProjects` from `@/hooks/use-projects`
    - Import `FolderOpen` from lucide-react
    - Add the submenu between "Copy Session ID" and the "Open in..." section (or after "New context window")
    - Submenu trigger: `<ContextMenuSubTrigger className="gap-2 text-xs"><FolderOpen className="h-3.5 w-3.5" /> Move to Project</ContextMenuSubTrigger>`
    - Submenu content: list all user projects (filter out `type === "scratch"`), plus an "Ungrouped" option that passes `null` as projectId
    - Each item shows the project name; the current project is indicated (checkmark or disabled)
    - On click: call `moveSession(session.id, projectId)`, then `refetch()`
    - Show the submenu only when there are projects to move to (at least 1 user project)
  **Acceptance**: Right-click session → "Move to Project" → submenu shows projects → selecting one moves the session → sidebar re-renders with session under new project.

### F) Project picker in New Session dialog

- [x] 10. **Add project picker to `NewSessionDialog`**
  **What**: Add an optional project dropdown/select to the New Session dialog so users can assign a session to a project at creation time.
  **Files**: `client/src/components/session/new-session-dialog.tsx`
  **Details**:
    - Import `useProjects` from `@/hooks/use-projects`
    - Import `Select`, `SelectTrigger`, `SelectValue`, `SelectContent`, `SelectItem` from `@/components/ui/select`
    - Add `selectedProjectId` state (default: empty string = no project / scratch)
    - Load projects via `useProjects()`, filter to `type !== "scratch"`
    - Only show the picker when there are user projects (if 0 user projects, skip the field entirely)
    - Place the picker after the "Title" field and before the "Branch" field
    - Label: "Project" with "(optional)" hint
    - SelectItems: one per project name + an "Ungrouped" option (value: empty string)
    - On submit: pass `projectId: selectedProjectId || undefined` into `createSession()`
    - Reset `selectedProjectId` to `""` when dialog closes
  **Acceptance**: New Session dialog shows project picker; selecting a project and creating a session assigns it correctly.

### G) Project reordering

- [x] 11. **Add move up/down to project context menu**
  **What**: Add "Move Up" and "Move Down" items to the project context menu. Uses `useReorderProject` hook. Disabled at boundaries (first item can't move up, last can't move down).
  **Files**: `client/src/components/layout/sidebar-project-item.tsx`
  **Details**:
    - Import `useReorderProject` from `@/hooks/use-reorder-project`
    - Import `ArrowUp`, `ArrowDown` from lucide-react
    - Add props: `projectIndex: number`, `projectCount: number` to `SidebarProjectItemProps` (passed from `fleet-panel.tsx`)
    - Context menu items (between Rename and Delete):
      - "Move Up" — disabled when `projectIndex === 0`; calls `reorderProject(projectId, currentPosition - 1)` then refetch
      - "Move Down" — disabled when `projectIndex === projectCount - 1`; calls `reorderProject(projectId, currentPosition + 1)` then refetch
    - Note: the exact position values depend on backend behavior. The backend's `ReorderProjectAsync` likely handles gap-free reordering, so passing `position - 1` / `position + 1` should work. Verify by checking `ProjectService.ReorderProjectAsync`.
  **Acceptance**: Right-click project → Move Up/Down → project position changes → sidebar re-renders in new order.

- [x] 12. **Pass reorder props from `FleetPanel` to `SidebarProjectItem`**
  **What**: `FleetPanel` needs to pass the project's index and total count of user-type projects (excluding ungrouped) so the context menu can enable/disable Move Up/Down correctly.
  **Files**: `client/src/components/layout/fleet-panel.tsx`
  **Details**:
    - Filter `projectGroups` to get only named (non-null projectId) groups for index/count
    - Pass `projectIndex` and `projectCount` to each `SidebarProjectItem`
    - Also pass `refetchProjects` callback
  **Acceptance**: Move Up/Down buttons are correctly enabled/disabled based on position.

## Verification

### Automated
- [x] `npx tsc --noEmit` — no new TypeScript errors
- [x] `npx vitest run` — all existing tests pass
- [x] `dotnet build` — backend compiles (if backend changes in task 1 are applied)

### Manual QA
- [x] Create a new project via the sidebar "New Project" button
- [x] Verify the project appears in the sidebar immediately after creation
- [x] Right-click the project → Rename → type new name → Enter → verify name changes
- [x] Double-click project name → rename works the same way
- [x] Right-click the project → Delete → select "Move sessions to Ungrouped" → confirm → project gone, sessions moved to Ungrouped
- [x] Create another project → add sessions → Delete with "Delete all sessions" mode → project and sessions gone
- [x] Right-click a session → "Move to Project" → select a project → session moves
- [x] Right-click a session → "Move to Project" → select "Ungrouped" → session moves to ungrouped
- [x] Open New Session dialog → verify Project dropdown shows → select a project → create session → session appears under that project
- [x] Open New Session dialog → leave Project as "Ungrouped" → create session → session appears in Ungrouped
- [x] Right-click project → Move Up/Down → verify project order changes in sidebar
- [x] Verify ungrouped bucket does NOT show context menu (no rename/delete on "Ungrouped")
- [x] Verify scratch-type projects don't appear in "Move to Project" submenu or project picker
- [x] Keyboard: navigate to project with arrow keys → press F2 → inline rename activates

### Edge Cases
- [x] Creating a project with an empty name is rejected (backend validation)
- [x] Deleting the last project doesn't crash the UI
- [x] Moving a session when there are 0 user projects — the "Move to Project" submenu should not appear
- [x] Very long project names truncate properly in sidebar
