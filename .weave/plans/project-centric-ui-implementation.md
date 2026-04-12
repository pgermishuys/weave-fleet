# Project-Centric UI Implementation

## TL;DR
> **Summary**: Full shell/navigation/IA restructure that makes Projects the primary navigation anchor, repositions Fleet as a cross-project dashboard, introduces a Data Sources page, and updates the icon rail, sidebar panels, routing, and command palette — all phased for incremental shipping.
> **Estimated Effort**: XL

## Context

### Original Request
Create a comprehensive, execution-ready implementation plan for the Weave Fleet project-centric UI redesign. This is a full shell/navigation/IA restructure covering: icon rail reorder, new routes (`/projects`, `/projects/:id`, `/fleet`, `/data-sources`), new components (ProjectCard, ProjectsPage, ProjectDetailPage, DataSourcesPage, ProjectsPanel, FleetLivePanel), existing component changes (FleetToolbar, page.tsx, sidebar-icon-rail, sidebar-panel, sidebar-context, settings/page, live-session-card), backend fixes (projectName enrichment, scratch rename guard), migration/redirects, empty states, and command palette updates.

### Key Findings

**Current routing (app.tsx lines 171-191):**
- `/` → FleetPage, `/sessions/:id` → SessionDetailPage, `/analytics`, `/settings`, `/integrations`, `/repositories`, `/repositories/:path`, `/welcome`, `/pipelines`, `/queue`, `/templates`
- No `/projects`, `/projects/:id`, `/fleet`, or `/data-sources` routes exist

**Current sidebar (sidebar-icon-rail.tsx):**
- Icon rail order: Logo→Fleet→(plugin items)→Repositories; bottom: Analytics, Settings
- `CORE_VIEW_DEFAULT_ROUTE`: welcome→`/welcome`, fleet→`/`, github→`/github`, repositories→`/repositories`
- `viewForPathname`: maps `/` and `/sessions/*` → "fleet", `/welcome` → "welcome", `/github*` → "github", `/repositories*` → "repositories"
- Logo click triggers `handleSwitch("welcome")`

**Current sidebar context (sidebar-context.tsx):**
- `SidebarView = string` (flexible)
- `CORE_PANEL_VIEWS`: fleet, repositories
- `LEGACY_PANEL_VIEWS`: fleet, github, repositories
- Panel visibility derived from `viewHasPanel(activeView)`

**Current sidebar panel (sidebar-panel.tsx):**
- Hardcoded rendering: `activeView === "fleet"` → FleetPanel, `activeView === "repositories"` → RepositoriesPanel
- Plugin panels rendered dynamically

**Current FleetPanel (fleet-panel.tsx):**
- Renders project tree with `SidebarProjectItem` nodes
- Header link goes to `/` ("Fleet")
- Uses `groupSessionsByProject()` from workspace-utils

**Current FleetToolbar (fleet-toolbar.tsx):**
- `GroupBy = "directory" | "session-status" | "connection-status" | "source" | "none"` — no "project"
- `DEFAULT_PREFS = { groupBy: "directory", sortBy: "recent" }`

**Current Fleet page (page.tsx):**
- Renders grouping for directory (SessionGroup), session-status, connection-status, source, none
- No project grouping implementation
- Empty state: "No sessions running" with "New Session" CTA

**Current live-session-card.tsx:**
- Link: `/sessions/${encodeURIComponent(session.id)}?instanceId=${encodeURIComponent(instanceId)}` — hardcoded, no project scope
- No `projectId` prop

**Current settings/page.tsx:**
- 10 tabs: Skills, Agents, Providers, Keybindings, Appearance, Harnesses, Integrations, Workspace Roots, API Keys, About

**Backend (SessionEndpoints.cs line 320):**
- `ProjectName: null` — comment says "enriched in Phase 3 project endpoints" but never implemented
- `ProjectId` is populated from session entity

**Backend (ProjectService.cs line 50-61):**
- `UpdateProjectAsync` does NOT guard against renaming scratch projects

**API types (api-types.ts):**
- `SessionListItem` has `projectId?: string | null` and `projectName?: string | null`
- `ProjectResponse` has `id, name, description, type, position, sessionCount, createdAt, updatedAt`

**Existing hooks (sufficient for this work):**
- `useProjects`, `useCreateProject`, `useUpdateProject`, `useDeleteProject`, `useReorderProject`, `useMoveSession`
- `useSessionsContext` (global session list), `useIntegrationsContext`

**Navigation commands (navigation-commands.tsx):**
- 6 commands registered: nav-fleet (→ `/`), nav-settings, nav-next-session, nav-prev-session, nav-most-recent, nav-go-to-session
- No "Go to Projects" or "Go to Data Sources"

**Existing tests (sidebar-icon-rail.test.ts):**
- Tests `viewForPathname` and `nextViewForSwitch` — will need updates for new views

## Objectives

### Core Objective
Transform navigation from session-centric (flat Fleet at `/`) to project-centric (Projects as primary anchor, Fleet as cross-project dashboard) while preserving every existing component, interaction pattern, and test.

### Deliverables
- [ ] New routes: `/projects`, `/projects/:id`, `/projects/:id/sessions/:sid`, `/fleet`, `/data-sources`
- [ ] New components: ProjectCard, ProjectsPage, ProjectDetailPage, DataSourcesPage, ProjectsPanel (evolved FleetPanel), FleetLivePanel
- [ ] Modified icon rail: Fleet → Projects → Data Sources → plugins → Analytics → Settings
- [ ] Modified FleetToolbar with "project" GroupBy option
- [ ] Modified Fleet page with project grouping and clickable group headers
- [ ] Modified Settings page (Integrations and Workspace Roots removed)
- [ ] Modified LiveSessionCard with optional project-scoped links
- [ ] Backend: projectName enrichment in session list, scratch rename guard
- [ ] Migration: redirects for `/`, `/repositories`, `/integrations`, `/sessions/:id`
- [ ] Command palette: "Go to Projects", "Go to Data Sources" commands
- [ ] Empty states for all new screens

### Definition of Done
- [ ] `npm run build` succeeds in `client/`
- [ ] `dotnet build -c Release` succeeds
- [ ] `npm run test` passes in `client/` (all existing + new tests)
- [ ] `/projects` renders project cards from real data
- [ ] `/projects/:id` renders project detail with sessions, data sources placeholder, activity, settings tabs
- [ ] `/fleet` renders Fleet dashboard with "Project" GroupBy (default)
- [ ] `/data-sources` renders data sources with Connected, Available, Workspace Roots tabs
- [ ] Icon rail shows: Fleet, Projects, Data Sources (replacing Repositories)
- [ ] Logo click → last active project or `/projects`
- [ ] Old URLs (`/`, `/sessions/:id`, `/repositories`, `/integrations`) still work via redirects
- [ ] No console errors on any route

### Guardrails (Must NOT)
- Rewrite LiveSessionCard, SessionGroup, FleetToolbar, SummaryBar, or Header
- Break existing `/sessions/:id` URLs
- Require new backend API endpoints (client-side data derivation for Phases 0-3)
- Remove the Welcome page (de-emphasize only)
- Change the plugin contribution model (FleetPluginManifest, contributions)
- Break mobile/responsive behavior
- Alter the theme system or color tokens
- Remove any existing test coverage

---

## TODOs

### Phase 0: Foundation (No Visible UI Change)

- [ ] 1. Add "project" to FleetToolbar GroupBy type
  **What**: Extend the `GroupBy` union type in `fleet-toolbar.tsx` from `"directory" | "session-status" | "connection-status" | "source" | "none"` to include `"project"`. Add `project: "Project"` to `GROUP_BY_LABELS`. Do NOT change the default yet — it stays `"directory"`.
  **Files**: `client/src/components/fleet/fleet-toolbar.tsx`
  **Acceptance**: TypeScript compiles. GroupBy dropdown includes "Project" option. Selecting it doesn't crash (even if grouping logic isn't wired yet — the Fleet page falls through to the default directory renderer).

- [ ] 2. Add project grouping logic to Fleet page
  **What**: In `page.tsx`, add a new `groupedByProject` memoized block (following the pattern of `groupedBySessionStatus`, `groupedByConnectionStatus`, `groupedBySource`). Import `useProjects` hook. Group `searchFiltered` sessions by `projectId`/`projectName`. For each project group, render a section with: clickable header (project name → `/projects/${projectId}`, session count badge), a session card grid using `LiveSessionCard`. Scratch/null-projectId sessions render in a "Scratch" group at the bottom with muted styling. Add `if (prefs.groupBy === "project") return groupedByProject;` to `renderContent()` before the default directory branch.
  **Files**: `client/src/app/page.tsx`
  **Acceptance**: Selecting "Project" in GroupBy dropdown groups sessions by project. Project group headers display project name and session count. Scratch group appears last.

- [ ] 3. Register `/fleet` route alias
  **What**: In `app.tsx`, add `<Route path="/fleet" element={<FleetPage />} />` after the existing `/` route. In `sidebar-icon-rail.tsx`, update `isFleetRoute()` to: `return pathname === "/" || pathname === "/fleet" || pathname.startsWith("/sessions");`. Update `CORE_VIEW_DEFAULT_ROUTE` to map `fleet: "/fleet"` (keeping `/` as a valid alias). In `viewForPathname`, add `if (pathname === "/fleet") return "fleet";`.
  **Files**: `client/src/app.tsx`, `client/src/components/layout/sidebar-icon-rail.tsx`
  **Acceptance**: Navigating to `/fleet` renders the Fleet page identically to `/`. Icon rail "Fleet" button highlights on both `/` and `/fleet`. Existing tests still pass (update `sidebar-icon-rail.test.ts`).

- [ ] 4. Register `/projects` and `/projects/:id` placeholder routes
  **What**: Create `client/src/app/projects/page.tsx` as a minimal placeholder: import `Header`, render `<Header title="Projects" subtitle="Coming soon" />` with a centered "Coming soon" message. Create `client/src/app/projects/[id]/page.tsx` similarly. In `app.tsx`, add lazy imports: `const ProjectsPage = lazy(() => import("./app/projects/page"));` and `const ProjectDetailPage = lazy(() => import("./app/projects/[id]/page"));`. Add routes: `<Route path="/projects" element={<ProjectsPage />} />` and `<Route path="/projects/:id" element={<ProjectDetailPage />} />`.
  **Files**: `client/src/app.tsx`, `client/src/app/projects/page.tsx` (new), `client/src/app/projects/[id]/page.tsx` (new)
  **Acceptance**: `/projects` and `/projects/some-id` render placeholder pages inside the layout shell. No 404s.

- [ ] 5. Register `/projects/:id/sessions/:sid` route
  **What**: In `app.tsx`, add `<Route path="/projects/:id/sessions/:sid" element={<SessionDetailPage />} />`. The existing `SessionDetailPage` component at `client/src/app/sessions/[id]/_page-client.tsx` reads `useParams()` for `id` — it will now receive `sid` from this route. Create a thin wrapper at `client/src/app/projects/[id]/sessions/[sid]/page.tsx` that extracts `sid` from params and renders `SessionDetailPage` (passing the correct session ID param). This wrapper should also render a breadcrumb back to `/projects/:id`.
  **Files**: `client/src/app.tsx`, `client/src/app/projects/[id]/sessions/[sid]/page.tsx` (new)
  **Acceptance**: `/projects/abc/sessions/xyz` renders the session detail page for session `xyz`. Back navigation goes to `/projects/abc`.

- [ ] 6. Register `/data-sources` placeholder route
  **What**: Create `client/src/app/data-sources/page.tsx` as a minimal placeholder. In `app.tsx`, add lazy import and route `<Route path="/data-sources" element={<DataSourcesPage />} />`.
  **Files**: `client/src/app.tsx`, `client/src/app/data-sources/page.tsx` (new)
  **Acceptance**: `/data-sources` renders a placeholder page.

- [ ] 7. Add "projects" view to sidebar context
  **What**: In `sidebar-context.tsx`, add `"projects"` to `CORE_PANEL_VIEWS` set (projects will have a panel). In `sidebar-icon-rail.tsx`, add `projects: "/projects"` to `CORE_VIEW_DEFAULT_ROUTE`. In `viewForPathname`, add: `if (pathname === "/projects" || pathname.startsWith("/projects/")) return "projects";` (place before the fleet check since `/projects` is more specific). Add `"data-sources": "/data-sources"` to `CORE_VIEW_DEFAULT_ROUTE` as a link-only view (no panel). In `viewForPathname`, add: `if (pathname === "/data-sources" || pathname.startsWith("/data-sources")) return "data-sources";`.
  **Files**: `client/src/contexts/sidebar-context.tsx`, `client/src/components/layout/sidebar-icon-rail.tsx`
  **Acceptance**: TypeScript compiles. `viewForPathname("/projects")` returns `"projects"`. `viewForPathname("/projects/abc")` returns `"projects"`. `viewForPathname("/data-sources")` returns `"data-sources"`. Update `sidebar-icon-rail.test.ts` with new test cases.

- [ ] 8. Update sidebar-icon-rail.test.ts for new views
  **What**: Add test cases to `viewForPathname` describe block: `/projects` → "projects", `/projects/abc` → "projects", `/projects/abc/sessions/xyz` → "projects", `/fleet` → "fleet", `/data-sources` → "data-sources". Add test cases to `nextViewForSwitch` for "projects" view (should toggle to "welcome" when clicked while active since it has a panel).
  **Files**: `client/src/components/layout/__tests__/sidebar-icon-rail.test.ts`
  **Acceptance**: `npm run test` passes with all new test cases.

---

### Phase 1: Projects Page + Icon Rail Changes

- [ ] 9. Build ProjectCard component
  **What**: Create `client/src/components/fleet/project-card.tsx`. Props: `project: ProjectResponse`, `sessionSummary: { total: number; active: number; idle: number; stopped: number; totalCost: number; lastActivity: number | null; statuses: Array<'working' | 'idle' | 'stopped'> }`, `onClick: () => void`. Layout: `Card` component with `hover:border-foreground/20 hover:shadow-md cursor-pointer`. Top: project icon (📓 for scratch type, `FolderOpen` for user) + project name (`font-semibold text-sm`). Right: context menu trigger (⋯) visible on hover. Middle: session count + active count (`text-xs text-muted-foreground`). Status pips row: max 8 colored dots (green=working, gray=idle, slate=stopped), then `+N`. Bottom-left: relative timestamp "Last: Xm ago". Bottom-right: cost badge (`text-xs font-mono`). Scratch variant: full-width, `bg-muted/50` background. Context menu: Rename, Edit Description, Delete (for non-scratch). Use `InlineEdit` for rename (already exists in `ui/inline-edit`).
  **Files**: `client/src/components/fleet/project-card.tsx` (new)
  **Acceptance**: Component renders with all visual zones. Scratch variant visually distinct. Context menu actions fire callbacks. Responsive: card fills grid column.

- [ ] 10. Build ProjectsPage with real data
  **What**: Replace placeholder in `client/src/app/projects/page.tsx`. Import `useProjects`, `useSessionsContext`. Derive per-project session summaries by filtering `sessions` by `projectId` (client-side). Layout: `Header` with title "Projects", subtitle showing total project count + active session count + today's cost, action button `[+ Project]` opening `CreateProjectDialog`. Below header: search bar (filter projects by name) + sort dropdown (Recent Activity, Name, Most Sessions). Content: Scratch card (full-width, pinned top, always visible even if 0 sessions). Grid of `ProjectCard` components (`grid-cols-1 md:grid-cols-2 xl:grid-cols-3`). Each card clickable → `navigate(/projects/${project.id})`. Empty state (no user projects): centered illustration + "Create your first project" CTA + "Or start an ad-hoc session in Scratch" secondary CTA.
  **Files**: `client/src/app/projects/page.tsx`
  **Acceptance**: Page renders real projects from API. Session summaries (count, active, cost) are correct. Creating a project refreshes list. Scratch appears first. Search filters work. Empty state renders when no user projects exist.

- [ ] 11. Update icon rail order and add Projects + Data Sources icons
  **What**: In `sidebar-icon-rail.tsx`, restructure the top section render order to: Fleet (`LayoutGrid`), Projects (`FolderOpen` from lucide — import it), then plugin items, then Data Sources (`Plug` from lucide — import it). Remove the `FolderGit2` Repositories button entirely. The "Projects" button uses `IconRailButton` with `view="projects"` and `onSwitch={handleSwitch}`. The "Data Sources" button uses `IconRailLink` with `href="/data-sources"` (it's a link, not a panel toggle). Specific render order:
  ```jsx
  <IconRailButton icon={LayoutGrid} label="Fleet" view="fleet" onSwitch={handleSwitch} />
  <IconRailButton icon={FolderOpen} label="Projects" view="projects" onSwitch={handleSwitch} />
  {pluginSidebarViews.map(…)}
  <IconRailLink icon={Plug} label="Data Sources" href="/data-sources" />
  ```
  Remove: `<IconRailButton icon={FolderGit2} label="Repositories" view="repositories" onSwitch={handleSwitch} />`
  **Files**: `client/src/components/layout/sidebar-icon-rail.tsx`
  **Acceptance**: Icon rail shows Fleet → Projects → (plugins) → Data Sources in top section. Analytics and Settings remain in bottom section. Repositories icon is gone. Projects icon opens projects panel. Data Sources icon navigates to `/data-sources`.

- [ ] 12. Wire ProjectsPanel into sidebar-panel.tsx
  **What**: The current `FleetPanel` already renders the project tree. We will reuse it as the "projects" panel. In `sidebar-panel.tsx`, update the panel content rendering: change `{activeView === "fleet" && <FleetPanel />}` to `{(activeView === "fleet" || activeView === "projects") && <FleetPanel />}`. Alternatively, rename the condition to `activeView === "projects"` and keep `activeView === "fleet"` for a future compact FleetLivePanel. For now, both "fleet" and "projects" views show the same FleetPanel (the project tree). Update the `aria-label` to include the "projects" case.
  **Files**: `client/src/components/layout/sidebar-panel.tsx`
  **Acceptance**: Clicking "Projects" in icon rail opens the panel showing the project tree (same as current Fleet panel). Clicking "Fleet" also still shows the project tree (until Phase 3 introduces FleetLivePanel).

- [ ] 13. Update FleetPanel header to be context-aware
  **What**: In `fleet-panel.tsx`, the header row currently says "Fleet" with a link to `/`. Update it: when `activeView === "projects"`, the header should say "Projects" and link to `/projects`. When `activeView === "fleet"`, it says "Fleet" and links to `/fleet`. Import `useSidebar` to read `activeView`. Update the header `<Link>` and icon accordingly: use `FolderOpen` for projects view, `LayoutGrid` for fleet view.
  **Files**: `client/src/components/layout/fleet-panel.tsx`
  **Acceptance**: Panel header changes based on active view. "Projects" mode links to `/projects`. "Fleet" mode links to `/fleet`. New Project and New Session buttons remain visible in both modes.

---

### Phase 2: Project Detail Page

- [ ] 14. Create useProject hook (single project fetch)
  **What**: Create `client/src/hooks/use-project.ts`. Takes a `projectId: string` parameter. Fetches `GET /api/projects/${projectId}`. Returns `{ project: ProjectResponse | null, isLoading: boolean, error?: string, refetch: () => void }`. Similar pattern to `useProjects` but for a single project. Returns 404-safe (returns null if not found).
  **Files**: `client/src/hooks/use-project.ts` (new)
  **Acceptance**: Hook fetches a single project by ID. Returns null for invalid IDs. Supports refetch.

- [ ] 15. Build ProjectDetailPage — Sessions tab
  **What**: Replace placeholder in `client/src/app/projects/[id]/page.tsx`. Import `useProject` (from new hook), `useSessionsContext`, `useProjects` (for userProjects list). Extract `id` from `useParams()`. Fetch project via `useProject(id)`. Filter sessions from `useSessionsContext()` by `projectId === id`. Header: back arrow (`ArrowLeft` → navigate to `/projects`), project name, session count + active count + cost subtitle, `[+ New Session]` button (opens `NewSessionDialog` with `defaultProjectId` set). Tabs (using `Tabs`/`TabsList`/`TabsTrigger`/`TabsContent` with `variant="line"`): Sessions (default), Data Sources, Activity, Settings. Sessions tab: group filtered sessions by status into Active (busy), Idle (running+idle), Stopped/Completed sections. Each section has a header (`text-xs font-semibold uppercase tracking-wider` with count). Render `LiveSessionCard` for each session. Card grid: `grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4`. All session action handlers (terminate, resume, delete, archive, unarchive, abort, open) follow the same pattern as Fleet page. Empty state: "No sessions in this project" + `[+ New Session]` CTA.
  **Files**: `client/src/app/projects/[id]/page.tsx`
  **Acceptance**: `/projects/:id` shows project detail. Sessions tab displays correct sessions grouped by status. All session actions work. New Session dialog pre-selects this project. Back arrow goes to `/projects`. 404-like state for invalid project IDs.

- [ ] 16. Add project-scoped session links to LiveSessionCard
  **What**: Add optional `projectId?: string` prop to `LiveSessionCard`. When `projectId` is provided, change the `<Link to=...>` from `/sessions/${session.id}?instanceId=${instanceId}` to `/projects/${projectId}/sessions/${session.id}?instanceId=${instanceId}`. When not provided, use the existing flat URL. The ProjectDetailPage passes `projectId={id}` to each card. The Fleet page does NOT pass projectId (keeps flat URLs for now, or passes projectId when grouped by project — implementer's choice).
  **Files**: `client/src/components/fleet/live-session-card.tsx`
  **Acceptance**: Session cards in project detail link to `/projects/:id/sessions/:sid`. Session cards in Fleet continue to use `/sessions/:id`. Existing tests pass.

- [ ] 17. Build project-scoped session detail wrapper
  **What**: Create `client/src/app/projects/[id]/sessions/[sid]/page.tsx`. This is a thin wrapper that: extracts `id` (projectId) and `sid` (sessionId) from `useParams()`. Renders the existing `SessionDetailPage` content but with a modified back-navigation that goes to `/projects/${id}` instead of `/`. Add a breadcrumb or back-link in the header area: "← Project Name" (fetch project name via `useProject`). The session detail itself reuses the full existing `SessionDetailPage` — either render it inline or use a shared component extraction. Simplest approach: the wrapper passes `sid` as the session ID and adds a back-navigation override.
  **Files**: `client/src/app/projects/[id]/sessions/[sid]/page.tsx` (already registered in Phase 0)
  **Acceptance**: `/projects/abc/sessions/xyz` renders session detail for session `xyz` with breadcrumb back to `/projects/abc`.

- [ ] 18. Project Detail — Data Sources tab (placeholder)
  **What**: Within the ProjectDetailPage's Data Sources tab content: render a placeholder message: "Data source attachment is coming soon." Below: "In the meantime, manage global data sources on the [Data Sources page](/data-sources)." — where "Data Sources page" is a `<Link>` to `/data-sources`. Use the empty state design pattern: icon (🔗 or `Link` icon), heading, body text, CTA link.
  **Files**: `client/src/app/projects/[id]/page.tsx` (within Tabs)
  **Acceptance**: Data Sources tab renders placeholder with link to `/data-sources`.

- [ ] 19. Project Detail — Activity tab
  **What**: Create `client/src/components/fleet/project-activity-feed.tsx`. Props: `projectId: string`. Uses `useSessionsContext()` to get all sessions, filters by `projectId`. Derives a timeline from session data: each entry shows relative timestamp, session title badge (linked to session), and a status description (e.g., "Session started", "Session went idle", "Session stopped"). Sort by most recent first. Limit to last 50 entries. For now, derive events from session metadata (`session.time.created`, `lifecycleStatus`, `activityStatus`) since we don't have a granular event stream per project. Render in the Activity tab of ProjectDetailPage.
  **Files**: `client/src/components/fleet/project-activity-feed.tsx` (new), `client/src/app/projects/[id]/page.tsx`
  **Acceptance**: Activity tab shows a timeline of recent session events for this project. Entries are sorted newest-first with relative timestamps.

- [ ] 20. Project Detail — Settings tab
  **What**: Within the ProjectDetailPage's Settings tab: show project metadata — Name (editable via `InlineEdit`, calls `useUpdateProject`), Description (editable textarea, calls `useUpdateProject`), Created date (formatted, read-only), Total Sessions count (from filtered sessions), Total Spend (summed from session costs). For scratch projects: Name field is read-only (not editable), no Delete button. For user projects: `[Delete Project]` button opens `ConfirmDeleteProjectDialog` (already exists at `client/src/components/fleet/confirm-delete-project-dialog.tsx`). On delete, navigate to `/projects`.
  **Files**: `client/src/app/projects/[id]/page.tsx`
  **Acceptance**: Can edit project name and description. Scratch project name is read-only. Delete navigates to `/projects`. Changes persist via API.

---

### Phase 3: Fleet Repositioning

- [ ] 21. Make project group headers clickable in Fleet
  **What**: In the `groupedByProject` block created in TODO 2, update each project group header to include a `<Link to={/projects/${projectId}}>` wrapping the project name, plus a `[→ Open Project]` link button. Style the header to indicate it's navigable: `hover:text-foreground cursor-pointer`. Scratch group header should NOT link to a project page (or link to `/projects/scratch-id` if scratch has a known ID).
  **Files**: `client/src/app/page.tsx`
  **Acceptance**: Clicking a project group header in Fleet (when grouped by Project) navigates to `/projects/:id`. Hover state indicates navigability.

- [ ] 22. Change default GroupBy to "project"
  **What**: In `fleet-toolbar.tsx`, change `DEFAULT_PREFS` from `{ groupBy: "directory", sortBy: "recent" }` to `{ groupBy: "project", sortBy: "recent" }`. Also update `loadSavedPrefs()` fallback from `parsed.groupBy ?? "directory"` to `parsed.groupBy ?? "project"`. Add a one-time migration in `loadSavedPrefs()`: if `localStorage` has the old `"directory"` default and no explicit user choice was made, migrate to `"project"`. Implementation: check if raw localStorage value exists; if not (null), return new defaults. If it exists with groupBy "directory", keep it (user explicitly chose directory). This ensures fresh installs get "project" default while existing users keep their preference.
  **Files**: `client/src/components/fleet/fleet-toolbar.tsx`
  **Acceptance**: Fresh installs (no localStorage) show Fleet grouped by Project. Existing users with explicit "directory" preference keep it. GroupBy dropdown still shows all options.

- [ ] 23. Move canonical Fleet route to `/fleet`
  **What**: In `app.tsx`, change the Fleet route from `<Route path="/" element={<FleetPage />} />` to `<Route path="/fleet" element={<FleetPage />} />`. Add a redirect route for `/`: create a `RootRedirect` component that checks `useProjects()` — if user has projects, redirect to `/projects`; else redirect to `/fleet`. Render: `<Route path="/" element={<RootRedirect />} />`. Update `CORE_VIEW_DEFAULT_ROUTE` in `sidebar-icon-rail.tsx`: `fleet: "/fleet"`. Update `isFleetRoute()`: `return pathname === "/fleet" || pathname.startsWith("/sessions");`. Remove `/` from `isFleetRoute`. Update `FleetPanel` header link from `to="/"` to `to="/fleet"`. Update navigation-commands.tsx: `goToFleet` from `navigate("/")` to `navigate("/fleet")`.
  **Files**: `client/src/app.tsx`, `client/src/components/layout/sidebar-icon-rail.tsx`, `client/src/components/layout/fleet-panel.tsx`, `client/src/components/commands/navigation-commands.tsx`
  **Acceptance**: `/fleet` is the canonical Fleet URL. `/` redirects to `/projects` or `/fleet`. All internal Fleet links point to `/fleet`. `sidebar-icon-rail.test.ts` updated.

- [ ] 24. Update logo click behavior
  **What**: In `sidebar-icon-rail.tsx`, change the logo button's `onClick` from `handleSwitch("welcome")` to navigate to the last active project. Add a `usePersistedState<string | null>("weave:lastProject", null)` hook. When a user navigates to `/projects/:id`, store that project ID (do this in `viewForPathname` effect or in a new effect). On logo click: if `lastProject` exists, `navigate(/projects/${lastProject})`; else `navigate("/projects")`. Keep the welcome view accessible at `/welcome` but remove it as a sidebar toggle target. Update the logo's `aria-pressed` and active indicator: use `isActive = pathname.startsWith("/projects")` instead of `activeView === "welcome"`.
  **Files**: `client/src/components/layout/sidebar-icon-rail.tsx`
  **Acceptance**: Logo click goes to last visited project or `/projects`. Logo no longer toggles welcome view. Welcome page still accessible via URL `/welcome`.

- [ ] 25. Build FleetLivePanel for fleet sidebar view
  **What**: Create `client/src/components/layout/fleet-live-panel.tsx`. This is a compact panel showing only live (running) sessions across all projects. Uses `useSessionsContext()`, filters to `lifecycleStatus === "running"`. Renders a simple list: each item shows status dot, session title (mono, truncated), project name below (sans, muted). Clicking a session navigates to `/sessions/:id?instanceId=:iid`. Sort by most recent activity. Header: "Live Sessions" with count badge. Empty state: "No active sessions." This replaces the full project tree that currently shows in the fleet panel. In `sidebar-panel.tsx`, update: `activeView === "fleet"` renders `FleetLivePanel` instead of `FleetPanel`. `activeView === "projects"` renders `FleetPanel` (the project tree).
  **Files**: `client/src/components/layout/fleet-live-panel.tsx` (new), `client/src/components/layout/sidebar-panel.tsx`
  **Acceptance**: Fleet icon in rail shows a compact live session list in the panel. Projects icon shows the full project tree. Both panels resize correctly.

---

### Phase 4: Data Sources Page

- [ ] 26. Build DataSourcesPage — Connected tab
  **What**: Replace placeholder in `client/src/app/data-sources/page.tsx`. Header: title "Data Sources", subtitle "Connect services that provide context for agent sessions". Tabs (line variant): Connected, Available, Workspace Roots. Connected tab: use `useIntegrationsContext()` to get `connectedIntegrations`. For each connected integration, render a Source Card: logo/icon area (integration icon from `getIntegration(id)?.icon`), service name (`font-semibold text-sm`), status indicator (✓ Connected in green), metadata line (e.g., for GitHub: repo count from `useGitHubRepos` — lazy loaded), scope line ("🌐 Global (all projects)"), action buttons: `[Browse]` (links to integration's route, e.g., `/github`), `[Disconnect]` (calls `disconnect(id)`). Source cards are full-width stacked (not grid). Empty state: "No data sources connected yet. Browse available integrations to connect your first source." with CTA to switch to Available tab.
  **Files**: `client/src/app/data-sources/page.tsx`
  **Acceptance**: Connected integrations display with correct status and actions. Browse navigates to integration routes. Disconnect works.

- [ ] 27. DataSourcesPage — Available tab
  **What**: In the Available tab content: use `useIntegrationsContext()` → `integrations.filter(i => i.status !== "connected")`. For each, render a simpler card: icon, name, brief description, `[Connect]` button (triggers existing OAuth/device flow via integration manifest). Grid layout: `grid-cols-2 md:grid-cols-3 lg:grid-cols-4`. Unconnected cards use `border-dashed bg-muted/30`. If all integrations are connected, show: "All available integrations are connected."
  **Files**: `client/src/app/data-sources/page.tsx` (within tabs)
  **Acceptance**: Available tab shows unconnected integrations. Connect button starts existing connection flow. Visual distinction from connected cards.

- [ ] 28. DataSourcesPage — Workspace Roots tab
  **What**: In the Workspace Roots tab content: reuse the existing `WorkspaceRootsTab` component from `client/src/components/settings/repositories-tab.tsx`. Simply import and render `<WorkspaceRootsTab />`. This immediately gives full workspace roots functionality (add, remove, scan) without reimplementation.
  **Files**: `client/src/app/data-sources/page.tsx`
  **Acceptance**: Workspace Roots tab renders the full existing workspace roots management UI. Add/remove roots works.

- [ ] 29. Add Data Sources icon to sidebar icon rail (if not done in Phase 1)
  **What**: Verify that the `IconRailLink` for Data Sources added in TODO 11 is functioning correctly. Ensure `viewForPathname("/data-sources")` returns `"data-sources"` and the icon highlights when on the data sources page. The Data Sources view does NOT have a panel (it's a link-only view like Analytics and Settings).
  **Files**: `client/src/components/layout/sidebar-icon-rail.tsx`
  **Acceptance**: Data Sources icon in rail highlights when on `/data-sources`. No panel opens.

---

### Phase 5: Navigation Polish and Cleanup

- [ ] 30. Remove Settings → Integrations and Workspace Roots tabs
  **What**: In `client/src/app/settings/page.tsx`, remove the `<TabsTrigger value="integrations">Integrations</TabsTrigger>` and `<TabsContent value="integrations">` blocks. Remove the `<TabsTrigger value="repositories">Workspace Roots</TabsTrigger>` and `<TabsContent value="repositories">` blocks. Remove the corresponding imports (`IntegrationsTab`, `WorkspaceRootsTab`). Update the subtitle to remove references to "workspace roots, integrations". Remaining tabs: Skills, Agents, Providers, Keybindings, Appearance, Harnesses, [API Keys], About. This drops from 10 tabs to 8 (or 7 without API Keys).
  **Files**: `client/src/app/settings/page.tsx`
  **Acceptance**: Settings page shows 7-8 tabs. No Integrations or Workspace Roots tabs. Existing functionality preserved in remaining tabs.

- [ ] 31. Add redirect for `/repositories` → `/data-sources?tab=roots`
  **What**: In `app.tsx`, replace the `/repositories` route with a redirect component: `<Route path="/repositories" element={<Navigate to="/data-sources?tab=roots" replace />} />`. Also replace `/repositories/:path` with a redirect to `/data-sources?tab=roots`. Remove the lazy imports for `RepositoriesPage` and `RepositoryDetailPage` if they are no longer needed (keep if repository detail browsing is still linked from Data Sources). Remove `"repositories"` from `CORE_VIEW_DEFAULT_ROUTE` in sidebar-icon-rail.tsx. Remove from `viewForPathname` the repositories case. Remove `RepositoriesPanel` import and rendering from `sidebar-panel.tsx`. Remove from `CORE_PANEL_VIEWS` and `LEGACY_PANEL_VIEWS` in sidebar-context.tsx.
  **Files**: `client/src/app.tsx`, `client/src/components/layout/sidebar-icon-rail.tsx`, `client/src/components/layout/sidebar-panel.tsx`, `client/src/contexts/sidebar-context.tsx`
  **Acceptance**: `/repositories` redirects to `/data-sources?tab=roots`. Repositories panel is gone from sidebar. No broken internal links.

- [ ] 32. Add redirect for `/integrations` → `/data-sources`
  **What**: In `app.tsx`, replace the `/integrations` route with: `<Route path="/integrations" element={<Navigate to="/data-sources" replace />} />`. Remove lazy import for `IntegrationsPage` if no longer needed.
  **Files**: `client/src/app.tsx`
  **Acceptance**: `/integrations` redirects to `/data-sources`.

- [ ] 33. Add `/sessions/:id` → project-scoped redirect
  **What**: In the existing `SessionDetailPage` (`client/src/app/sessions/[id]/_page-client.tsx`), add redirect logic: after loading session metadata, check if the session has a `projectId`. If it does, and the current URL is `/sessions/:id` (not already project-scoped), use `navigate(`/projects/${projectId}/sessions/${sessionId}?instanceId=${instanceId}`, { replace: true })`. If no projectId, render the session inline as before. This makes old bookmarks redirect to project-scoped URLs without breaking.
  **Files**: `client/src/app/sessions/[id]/_page-client.tsx`
  **Acceptance**: Navigating to `/sessions/xyz` automatically redirects to `/projects/{projectId}/sessions/xyz` if the session has a project. Sessions without a project render at the flat URL.

- [ ] 34. Update command palette
  **What**: In `navigation-commands.tsx`: Add "Go to Projects" command: `{ id: "nav-projects", label: "Go to Projects", icon: FolderOpen, category: "Navigation", keywords: ["project", "list"], action: () => navigate("/projects") }`. Add "Go to Data Sources" command: `{ id: "nav-data-sources", label: "Go to Data Sources", icon: Plug, category: "Navigation", keywords: ["integrations", "sources", "plugins", "connect"], action: () => navigate("/data-sources") }`. Update "Go to Fleet" action: change `navigate("/")` to `navigate("/fleet")`. Add cleanup: `unregisterCommand("nav-projects")` and `unregisterCommand("nav-data-sources")`. Also update `keybindings-tab.tsx` to add the new navigation entries to the displayed keybindings list.
  **Files**: `client/src/components/commands/navigation-commands.tsx`, `client/src/components/settings/keybindings-tab.tsx`
  **Acceptance**: Command palette shows "Go to Projects", "Go to Data Sources", and "Go to Fleet" (targeting `/fleet`). All three navigate correctly.

- [ ] 35. Add empty states for new screens
  **What**: Verify and polish empty states across all new pages:
  - **Projects list** (no user projects, only scratch): centered empty state with `FolderOpen` icon, "No projects yet" heading, "Projects organize related AI sessions into focused workstreams." body, `[+ Create Project]` primary CTA, "Or start an ad-hoc session in Scratch → Open Scratch" secondary link.
  - **Project detail sessions tab** (no sessions): centered empty state with `MessageSquare` icon, "No sessions in this project" heading, "Start your first AI session to begin working on {projectName}." body, `[+ New Session]` CTA.
  - **Project detail data sources tab**: already handled in TODO 18 (placeholder).
  - **Data Sources connected tab** (nothing connected): centered empty state with `Plug` icon, "No data sources connected" heading, "Connect external services to provide context for your AI sessions." body, `[Browse Available Sources →]` CTA (switches to Available tab).
  - **Fleet empty state** (no sessions): update existing empty state to add secondary CTA: `[→ Go to Projects]` link below the existing "New Session" message.
  Empty state design tokens: `flex flex-col items-center justify-center text-center gap-3 py-12`, icon `h-12 w-12 text-muted-foreground/30`, heading `text-sm font-medium text-foreground`, body `text-xs text-muted-foreground leading-relaxed max-w-xs`, CTA `Button size="sm"`.
  **Files**: `client/src/app/projects/page.tsx`, `client/src/app/projects/[id]/page.tsx`, `client/src/app/data-sources/page.tsx`, `client/src/app/page.tsx`
  **Acceptance**: All empty states render with correct design tokens. CTAs navigate to correct targets.

- [ ] 36. De-emphasize Welcome from primary nav
  **What**: The Welcome page (`/welcome`) remains accessible via URL but is no longer reachable from the sidebar. Logo click now goes to projects (handled in TODO 24). Remove `"welcome"` from `CORE_VIEW_DEFAULT_ROUTE` (it's no longer a sidebar-navigable view). In `viewForPathname`, keep `if (pathname === "/welcome") return "welcome";` so the URL still works. In `SidebarProvider`, change the default `lastPanelViewRef` from `"fleet"` to `"projects"` (the new primary panel view). The `toggleSidebar` function's restore behavior should restore to "projects" instead of "fleet" by default.
  **Files**: `client/src/components/layout/sidebar-icon-rail.tsx`, `client/src/contexts/sidebar-context.tsx`
  **Acceptance**: `/welcome` still renders. No way to navigate to welcome from sidebar. ⌘B toggle restores "projects" panel.

---

### Phase 6: Backend Fixes (Minor)

- [ ] 37. Enrich projectName in session list endpoint
  **What**: In `SessionEndpoints.cs`, the `ToListResponse()` method currently sets `ProjectName: null`. Fix this by: in the `GET /api/sessions` handler, after fetching sessions, load all projects via `ProjectService.ListProjectsAsync()`. Build a `Dictionary<string, string>` of projectId → projectName. In `ToListResponse`, accept this dictionary as a parameter and look up the project name: `ProjectName: projectNameMap.TryGetValue(s.ProjectId ?? "", out var name) ? name : null`. This is a simple in-memory join — no new SQL queries needed beyond the existing `ListProjectsAsync`.
  **Files**: `src/WeaveFleet.Api/Endpoints/SessionEndpoints.cs`
  **Acceptance**: `GET /api/sessions` response items include `projectName` when the session has a `projectId`. Existing session list continues to work. Frontend `SessionListItem.projectName` is populated.

- [ ] 38. Guard scratch project rename
  **What**: In `ProjectService.UpdateProjectAsync()`, add a guard: if `project.Type == "scratch"` and `name` is not null and `name != project.Name`, return `FleetError.ValidationError("Name", "Cannot rename the scratch project.")`. Allow description updates for scratch projects.
  **Files**: `src/WeaveFleet.Application/Services/ProjectService.cs`
  **Acceptance**: API rejects rename of scratch project with 400 error. Description updates still work. Add unit test in `ProjectServiceTests.cs`.

- [ ] 39. Add scratch rename guard test
  **What**: In `tests/WeaveFleet.Application.Tests/Services/ProjectServiceTests.cs`, add test: `UpdateProjectAsync_RejectsScratchRename`. Setup: mock project repo returns a scratch project. Call `UpdateProjectAsync` with a new name. Assert: result is error with "Cannot rename the scratch project." message.
  **Files**: `tests/WeaveFleet.Application.Tests/Services/ProjectServiceTests.cs`
  **Acceptance**: Test passes. No regressions in existing project tests.

---

## Verification

- [ ] All existing Vitest tests pass: `npm run test` in `client/`
- [ ] All existing .NET tests pass: `dotnet test` in repo root
- [ ] New pages render correctly at 1440w, 768w, and 360w viewpoints (manual check)
- [ ] `npm run build` succeeds in `client/` (no TypeScript errors, no dead code warnings)
- [ ] `dotnet build -c Release` succeeds
- [ ] Keyboard navigation: Tab through icon rail items, Enter to select, panel opens/closes correctly
- [ ] Screen reader: all new pages have `aria-label`, `role`, heading hierarchy (h2 for page title, section headers)
- [ ] No console errors on any new route (`/projects`, `/projects/:id`, `/fleet`, `/data-sources`)
- [ ] `/sessions/:id` still renders correctly (backwards compat redirect)
- [ ] `/repositories` redirects to `/data-sources?tab=roots`
- [ ] `/integrations` redirects to `/data-sources`
- [ ] `/` redirects to `/projects` or `/fleet` appropriately
- [ ] Fleet page with "Project" grouping handles: 0 projects, 1 project, 10+ projects, sessions without projectId
- [ ] Empty states render on each new page when no data exists
- [ ] Command palette "Go to Projects", "Go to Data Sources", "Go to Fleet" all work
- [ ] Logo click navigates to last active project (or `/projects` if none)
- [ ] Mobile (< 717px): sidebar drawer works with new icon rail order, all new pages scroll correctly
