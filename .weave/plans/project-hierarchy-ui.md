# Project-Session Hierarchy in Fleet Panel Sidebar

## TL;DR
> **Summary**: Add a collapsible project grouping layer above the existing workspace grouping in the Fleet Panel sidebar, so sessions appear nested as Project → Workspace → Session.
> **Estimated Effort**: Short

## Context
### Original Request
The Fleet Panel sidebar currently groups sessions by workspace only. We need an intermediate project grouping layer: sessions with a `projectId` are grouped under their project header, and sessions without a `projectId` (or belonging to a "scratch" project) fall into an "Ungrouped" section at the bottom.

### Key Findings
- **`SessionListItem`** already carries `projectId?: string | null` and `projectName?: string | null` — no API changes needed.
- **`useProjects()`** hook exists at `client/src/hooks/use-projects.ts`, fetches `/api/projects`, returns `ProjectResponse[]` with `id`, `name`, `type`, `position` — currently unused.
- **`SidebarWorkspaceItem`** is fully self-contained — takes a `WorkspaceGroup` and renders everything. No changes needed to reuse it inside a project group.
- **`groupSessionsByWorkspace()`** in `workspace-utils.ts` is a pure function — can be called per-project-subset independently.
- **Collapsible pattern** is well-established: `session-group.tsx` uses `Collapsible` + `usePersistedState<string[]>` with a collapsed-IDs array. We'll follow the same pattern.
- **`fleet-panel.tsx`** is the only file that orchestrates the workspace list — it calls `useWorkspaces(sessions)` and maps over the result. This is the primary integration point.

## Objectives
### Core Objective
Introduce a project grouping layer in the sidebar so sessions are visually organized as **Project → Workspace → Session**, with ungrouped sessions at the bottom.

### Deliverables
- [x] Pure utility function `groupSessionsByProject()` in `workspace-utils.ts`
- [x] New `SidebarProjectItem` component for collapsible project headers
- [x] Updated `FleetPanel` to render project groups instead of flat workspace list
- [x] Unit tests for `groupSessionsByProject()`

### Definition of Done
- [ ] `pnpm test -- workspace-utils` passes (new + existing tests)
- [ ] `pnpm tsc --noEmit` passes with no type errors
- [ ] Sidebar renders project → workspace → session hierarchy when sessions have `projectId`
- [ ] Sessions with `null`/`undefined` projectId or scratch-type project appear under "Ungrouped" at bottom
- [ ] Project sections are independently collapsible with state persisted across reloads

### Guardrails (Must NOT)
- Do NOT modify `SidebarWorkspaceItem` — reuse as-is
- Do NOT add drag-and-drop, project CRUD, or move-session-to-project features
- Do NOT introduce new data fetching — only use `useProjects()` and `useSessionsContext()`
- Do NOT change `WorkspaceGroup` interface or `groupSessionsByWorkspace()` behavior

## TODOs

- [x] 1. **Add `groupSessionsByProject()` to workspace-utils**
  **What**: Add a new pure function and its associated `ProjectGroup` interface to `client/src/lib/workspace-utils.ts`. The function takes `sessions: SessionListItem[]` and `projects: ProjectResponse[]` and returns `ProjectGroup[]`.

  ```ts
  export interface ProjectGroup {
    projectId: string | null;      // null = ungrouped
    projectName: string;           // "Ungrouped" for null bucket
    position: number;              // from ProjectResponse.position; Infinity for ungrouped
    workspaces: WorkspaceGroup[];  // result of groupSessionsByWorkspace() on this project's sessions
  }
  ```

  Logic:
  1. Build a `Map<string | null, SessionListItem[]>` keyed by `session.projectId` (treat `undefined` as `null`).
  2. Identify scratch projects: iterate `projects`, if `project.type === "scratch"`, merge its sessions into the `null` bucket.
  3. For each non-null, non-scratch project key, look up the `ProjectResponse` to get `name` and `position`. If the project ID is on sessions but not in the `projects` array, use `session.projectName` as fallback name and `Infinity - 1` as position.
  4. Call `groupSessionsByWorkspace(bucketSessions)` for each bucket to produce `workspaces`.
  5. Sort result by `position` ascending. Ungrouped (`null`) always last (position = `Infinity`).
  6. Omit empty groups (no sessions).

  **Files**: `client/src/lib/workspace-utils.ts`
  **Acceptance**: Function is exported, takes the documented signature, and passes all unit tests in TODO #2.

- [x] 2. **Add unit tests for `groupSessionsByProject()`**
  **What**: Add a new `describe("groupSessionsByProject", ...)` block to the existing test file `client/src/lib/__tests__/workspace-utils.test.ts`. Use the existing `makeSession()` helper, extending it with `projectId`/`projectName` overrides.

  Test cases:
  - Empty sessions → `[]`
  - All sessions have same projectId → single ProjectGroup with correct workspaces
  - Mixed projectIds → multiple ProjectGroups, sorted by position
  - Sessions with `projectId: null` and `projectId: undefined` → both land in Ungrouped
  - Scratch-type project sessions merge into Ungrouped bucket
  - Ungrouped always sorts last regardless of other positions
  - Sessions with projectId not in `projects[]` array → uses `projectName` from session as fallback
  - Workspace sub-grouping works correctly within each project group (sessions in same directory merge)

  **Files**: `client/src/lib/__tests__/workspace-utils.test.ts`
  **Acceptance**: `pnpm test -- workspace-utils` passes with all new tests green.

- [x] 3. **Create `SidebarProjectItem` component**
  **What**: Create `client/src/components/layout/sidebar-project-item.tsx`. This is a collapsible wrapper that renders a project header and its child `SidebarWorkspaceItem` components.

  Structure:
  ```tsx
  interface SidebarProjectItemProps {
    group: ProjectGroup;
    activeSessionPath: string;
    refetch: () => void;
  }
  ```

  Implementation details:
  - Use `Collapsible` / `CollapsibleTrigger` / `CollapsibleContent` from `@/components/ui/collapsible` (same pattern as `session-group.tsx`).
  - Persist collapsed state via `usePersistedState<string[]>("weave:sidebar:project-collapsed", [])`. Key each project by `projectId ?? "ungrouped"`.
  - **Project header row**: `ChevronRight` icon (rotates 90° when open), project name in `text-xs font-medium`, session count badge showing total sessions across all workspaces in the group. Use `group/header` pattern for hover-reveal styling.
  - For "Ungrouped" (`projectId === null`), render the name as "Ungrouped" with slightly muted styling (`text-muted-foreground`), no folder icon.
  - For named projects, render `FolderOpen` (from lucide-react) icon next to the name.
  - **Children**: Map over `group.workspaces` and render `<SidebarWorkspaceItem>` for each, passing through `activeSessionPath` and `refetch`. Add `pl-2` to indent workspaces under the project header.
  - Wrap in `React.memo`.
  - The component supports keyboard navigation — the project header should have `role="treeitem"` and `tabIndex={0}`.

  **Files**: `client/src/components/layout/sidebar-project-item.tsx` (new file)
  **Acceptance**: Component renders, collapses/expands, persists state. TypeScript compiles cleanly.

- [x] 4. **Update `FleetPanel` to use project grouping**
  **What**: Modify `client/src/components/layout/fleet-panel.tsx` to group sessions by project before rendering.

  Changes:
  1. Add imports: `useProjects` from `@/hooks/use-projects`, `groupSessionsByProject` from `@/lib/workspace-utils`, `SidebarProjectItem` from `./sidebar-project-item`, and `useMemo`.
  2. Call `const { projects } = useProjects();` alongside existing `useSessionsContext()`.
  3. Replace `const workspaces = useWorkspaces(sessions);` with:
     ```ts
     const projectGroups = useMemo(
       () => groupSessionsByProject(sessions, projects),
       [sessions, projects]
     );
     ```
  4. In the JSX, replace the `workspaces.map(...)` block with:
     ```tsx
     projectGroups.map((group) => (
       <SidebarProjectItem
         key={group.projectId ?? "ungrouped"}
         group={group}
         activeSessionPath={pathname}
         refetch={refetch}
       />
     ))
     ```
  5. Update the empty-state check: `projectGroups.length === 0` instead of `workspaces.length === 0`. Update the empty message from "No workspaces yet" to "No sessions yet".
  6. Update `aria-label` on the tree div from "Workspaces" to "Sessions".
  7. Remove the now-unused `useWorkspaces` import (but keep it available in the codebase for other consumers).

  **Files**: `client/src/components/layout/fleet-panel.tsx`
  **Acceptance**: Sidebar renders the project → workspace → session hierarchy. Keyboard navigation still works (arrow keys traverse project headers, workspace items, and session items). The `useWorkspaces` import is removed from this file only.

## Verification
- [x] `pnpm tsc --noEmit` — no type errors across the project
- [x] `pnpm test -- workspace-utils` — all existing + new tests pass
- [ ] Manual: sidebar shows project headers when sessions have different `projectId` values
- [ ] Manual: sessions without `projectId` appear under "Ungrouped" at the bottom
- [ ] Manual: collapsing a project persists across page reload
- [ ] Manual: keyboard navigation (arrow keys) still traverses the full tree
- [ ] No regressions in `SidebarWorkspaceItem` behavior (rename, pin, context menu)
