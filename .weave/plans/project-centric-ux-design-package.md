# Project-Centric UX Design Package

## TL;DR
> **Summary**: A practical mid-fidelity design package that expands the project-centric UX concept into wireframe specifications for four key screens (Projects list, Project detail, Fleet dashboard, Data Sources), a cohesive visual design direction, and a phased implementation plan that evolves the existing app incrementally — no rewrites. This document is the bridge between concept and code.
> **Estimated Effort**: Large

## Context

### Original Request
Take the existing project-centric UX concept (`.weave/plans/ux-concept-project-centric.md`) and expand it into a practical design package. Deliverables: (1) mid-fidelity wireframe descriptions, (2) visual design direction, (3) phased implementation plan.

### Key Findings (Codebase Audit)

**Current routing & layout:**
- `App.tsx` mounts routes inside `ClientLayout` which provides `SwipeableLayout` (sidebar + main)
- Routes: `/` (Fleet), `/sessions/:id`, `/analytics`, `/settings`, `/integrations`, `/repositories`, `/repositories/:path`, `/welcome`, `/pipelines`, `/queue`, `/templates`
- No project-scoped routes exist yet — no `/projects` or `/projects/:id`
- Session URLs are flat: `/sessions/:id` with `?instanceId=` query param

**Current sidebar architecture:**
- `SidebarIconRail` (48px) — Logo/Welcome, Fleet, plugin-contributed items, Repositories; bottom: Analytics, Settings
- `SidebarProvider` manages `activeView` and panel visibility via `viewHasPanel()`
- `FleetPanel` is the contextual panel for the "fleet" view — renders project tree with `SidebarProjectItem` → `SidebarWorkspaceItem` → `SidebarSessionItem`
- Panel views: `fleet`, `github`, `repositories` (from `LEGACY_PANEL_VIEWS`)
- `ContextualPanel` renders the correct panel based on `activeView`

**Current component inventory (relevant to redesign):**
- `LiveSessionCard` — mature, 296 lines, handles all session states/badges/actions
- `SessionGroup` — groups by workspace/directory with collapsible headers, inline rename
- `FleetToolbar` — GroupBy (`directory | session-status | connection-status | source | none`), SortBy, RetentionFilter
- `SummaryBar` — 4-stat grid (Active, Idle, Tokens, Queued)
- `Header` — 56px, title/subtitle/actions, mobile hamburger, user menu
- `SidebarProjectItem` — collapsible project node with context menu (rename, reorder, delete)
- `CreateProjectDialog` — dialog for creating new projects
- `NewSessionDialog` — already accepts `userProjects` for project assignment

**Current design tokens (from globals.css):**
- Font stack: Inter (sans), JetBrains Mono (mono)
- Color: dark-first (slate-900 base `#0F172A`), 8 themes (dark, black, light, nord, dracula, solarized-dark, solarized-light, monokai, github-dark)
- Spacing: uses Tailwind gap-2, gap-3, p-3/p-4/p-6 responsive progression
- Card radius: `--radius: 0.625rem` (10px)
- Brand: weave-gradient (blue→purple→pink), `--color-primary: #A855F7` (purple)
- Session status: green-500 for working, muted-foreground/50 for idle

**Backend readiness:**
- `Project` entity exists with `Id, Name, Description, Type (user|scratch), Position, UserId`
- API: `GET /api/projects`, `POST /api/projects`, `PUT /api/projects/:id`, `DELETE /api/projects/:id`, `PUT /api/projects/:id/position`
- Sessions already carry `projectId` and `projectName`
- `EnsureScratchProjectAsync` auto-creates the scratch project on startup
- Hooks: `useProjects`, `useUpdateProject`, `useDeleteProject`, `useReorderProject`

**What does NOT exist yet:**
- No `/projects` list page
- No `/projects/:id` detail page
- No "Project" GroupBy option in FleetToolbar
- No Data Sources top-level page (integrations are inside Settings)
- No project-scoped session URLs (`/projects/:id/sessions/:sessionId`)
- No sidebar "Projects" panel (FleetPanel is called "Fleet")

---

## Part 1: Mid-Fidelity Wireframe Specifications

### Screen 1: Projects List (`/projects`)

**Purpose:** Home base. See all projects at a glance, create new ones, jump into any project. This is the landing page for users who think in projects (the 80% path).

**Layout:** Full-width main content, no secondary panel needed.

```
╔══════════════════════════════════════════════════════════════════════╗
║  Header (56px)                                                       ║
║  ┌────────────────────────────────────────────────────────────────┐   ║
║  │  📁 Projects                                      [+ Project]  │   ║
║  │  5 projects · 12 active sessions · $18.50 today                │   ║
║  └────────────────────────────────────────────────────────────────┘   ║
╠══════════════════════════════════════════════════════════════════════╣
║  Content (scrollable, p-4 lg:p-6)                                    ║
║                                                                      ║
║  ┌─ Search & Filter Bar ──────────────────────────────────────────┐  ║
║  │  [🔍 Search projects…]          Sort: [Recent Activity ▾]      │  ║
║  └────────────────────────────────────────────────────────────────┘  ║
║                                                                      ║
║  ═══ Pinned ═════════════════════════════════════════════════════    ║
║                                                                      ║
║  ┌─ Scratch ──────────────────────────────────────────────────────┐  ║
║  │  📓 Scratch                                     3 sessions     │  ║
║  │  ┌──────┐ ┌──────┐ ┌──────┐                                   │  ║
║  │  │● q.. │ │○ fi..│ │■ on..│  ← inline session thumbnails      │  ║
║  │  └──────┘ └──────┘ └──────┘                                   │  ║
║  │  Last activity: 4m ago                          [$0.82 today]  │  ║
║  └────────────────────────────────────────────────────────────────┘  ║
║                                                                      ║
║  ═══ Projects ═══════════════════════════════════════════════════    ║
║                                                                      ║
║  ┌─ grid layout: 1-col mobile, 2-col md, 3-col xl ───────────────┐  ║
║  │                                                                 │  ║
║  │  ┌─────────────────────────┐  ┌─────────────────────────┐      │  ║
║  │  │ 📂 Auth Rewrite        │  │ 📂 Q4 Campaign          │      │  ║
║  │  │                        │  │                          │      │  ║
║  │  │ 5 sessions · 2 active  │  │ 3 sessions · 0 active   │      │  ║
║  │  │ ●● ○ ■ ■              │  │ ○ ■ ■                   │      │  ║
║  │  │                        │  │                          │      │  ║
║  │  │ Last: 2m ago           │  │ Last: 3h ago             │      │  ║
║  │  │ $4.23 today            │  │ $1.10 today              │      │  ║
║  │  └─────────────────────────┘  └─────────────────────────┘      │  ║
║  │                                                                 │  ║
║  │  ┌─────────────────────────┐  ┌─────────────────────────┐      │  ║
║  │  │ 📂 Financial Analysis  │  │ 📂 API Redesign         │      │  ║
║  │  │                        │  │                          │      │  ║
║  │  │ 4 sessions · 1 active  │  │ 0 sessions              │      │  ║
║  │  │ ● ○ ○ ■               │  │                          │      │  ║
║  │  │                        │  │ No sessions yet.         │      │  ║
║  │  │ Last: 14m ago          │  │ Created yesterday        │      │  ║
║  │  │ $8.92 today            │  │                          │      │  ║
║  │  └─────────────────────────┘  └─────────────────────────┘      │  ║
║  │                                                                 │  ║
║  └─────────────────────────────────────────────────────────────────┘  ║
╚══════════════════════════════════════════════════════════════════════╝
```

**Project Card Anatomy (≈200×160px @1440w, 3 col):**

| Zone | Content | Tokens |
|---|---|---|
| Top-left | Project icon (📂 folder or 📓 notepad for Scratch) + Project name (font-semibold text-sm) | truncate at 20ch |
| Top-right | Context menu trigger (⋯) — rename, delete, edit description | ghost, hover-visible |
| Middle | Session count + active count ("5 sessions · 2 active") | text-xs text-muted-foreground |
| Middle | Session status pips: row of small dots (●○■) — max 8, then "+N" | ● green, ○ gray, ■ slate |
| Bottom-left | "Last: 2m ago" relative timestamp | text-xs text-muted-foreground |
| Bottom-right | "$4.23 today" cost badge (only if >$0) | text-xs, monospaced |

**Interactions:**
- Click card → navigate to `/projects/:id`
- Click "+" button → `CreateProjectDialog` (already exists)
- Context menu → Rename, Edit Description, Delete (already have hooks)
- Scratch card is always first, full-width, slightly muted background to signal "catch-all"
- Empty state (no projects): centered illustration + "Create your first project" CTA

**Data requirements:**
- `GET /api/projects` already returns `id, name, type, sessionCount`
- Need: aggregate active session count per project, total cost per project today
- Source: derive from `sessions` array filtered by `projectId` (client-side initially)

---

### Screen 2: Project Detail (`/projects/:id`)

**Purpose:** The 80%-time screen. Everything about one body of work — its sessions, attached context, recent activity.

```
╔══════════════════════════════════════════════════════════════════════╗
║  Header (56px)                                                       ║
║  ┌────────────────────────────────────────────────────────────────┐   ║
║  │  [←] Auth Rewrite                      [+ New Session] [⚙]    │   ║
║  │      5 sessions · 2 active · $4.23 today                       │   ║
║  └────────────────────────────────────────────────────────────────┘   ║
╠══════════════════════════════════════════════════════════════════════╣
║  Tabs (line variant, below header)                                   ║
║  ┌────────────────────────────────────────────────────────────────┐   ║
║  │  [Sessions]  [Data Sources]  [Activity]  [Settings]            │   ║
║  └────────────────────────────────────────────────────────────────┘   ║
╠══════════════════════════════════════════════════════════════════════╣
║  Content — Sessions Tab (default)                                    ║
║                                                                      ║
║  ── Active (2) ──────────────────────────────────────────────────    ║
║                                                                      ║
║  ┌──────────────────┐  ┌──────────────────┐                         ║
║  │ ● fix-login      │  │ ● refactor-auth  │                         ║
║  │   working         │  │   working         │                         ║
║  │   3.2k tok $0.42 │  │   890 tok  $0.12 │                         ║
║  │   started 2m ago │  │   started <1m ago│                         ║
║  │   [conductor]    │  │   [worktree 🌿]  │                         ║
║  └──────────────────┘  └──────────────────┘                         ║
║                                                                      ║
║  ── Idle (1) ────────────────────────────────────────────────────    ║
║                                                                      ║
║  ┌──────────────────┐                                               ║
║  │ ○ add-tests      │                                               ║
║  │   idle            │                                               ║
║  │   1.1k tok $0.14 │                                               ║
║  │   last msg 14m   │                                               ║
║  └──────────────────┘                                               ║
║                                                                      ║
║  ── Stopped (2) ─────────────────────────────────────────────────    ║
║                                                                      ║
║  ┌──────────────────┐  ┌──────────────────┐                         ║
║  │ ■ auth-flow      │  │ ■ db-schema      │  ← reduced opacity     ║
║  │   stopped         │  │   completed       │                         ║
║  │   12k tok  $1.55 │  │   8.4k tok $2.00 │                         ║
║  │   stopped 2h ago │  │   done yesterday  │                         ║
║  │   [Resume]       │  │   [Resume]        │                         ║
║  └──────────────────┘  └──────────────────┘                         ║
║                                                                      ║
╠══════════════════════════════════════════════════════════════════════╣
║  Content — Data Sources Tab                                          ║
║                                                                      ║
║  ── Attached to this project ───────────────────────────────────     ║
║  ┌────────────────────────────────────────────────────────────────┐  ║
║  │ [GH] weave-dev/auth-service       ✓ Connected      [Detach]   │  ║
║  │ [N]  Auth Rewrite spec page       ✓ Connected      [Detach]   │  ║
║  └────────────────────────────────────────────────────────────────┘  ║
║  [+ Attach Data Source]                                              ║
║                                                                      ║
║  ── Inherited (global) ─────────────────────────────────────────     ║
║  ┌────────────────────────────────────────────────────────────────┐  ║
║  │ [WS] ~/code (workspace root)      available globally           │  ║
║  └────────────────────────────────────────────────────────────────┘  ║
║                                                                      ║
╠══════════════════════════════════════════════════════════════════════╣
║  Content — Activity Tab                                              ║
║                                                                      ║
║  Timeline of recent events across all sessions in this project.      ║
║  Each entry: [timestamp] [session badge] [event description]         ║
║                                                                      ║
║  • 2m ago   fix-login    Tool call: edit_file (src/auth.ts)          ║
║  • 3m ago   refactor     Created worktree branch                     ║
║  • 14m ago  add-tests    Session went idle                           ║
║  • 2h ago   auth-flow    Session stopped                             ║
║                                                                      ║
╠══════════════════════════════════════════════════════════════════════╣
║  Content — Settings Tab                                              ║
║                                                                      ║
║  Project Name:    [Auth Rewrite          ]                           ║
║  Description:     [Rewrite authentication to OAuth2... ]             ║
║  Created:         April 8, 2026                                      ║
║  Total Sessions:  12 (5 active, 2 archived)                          ║
║  Total Spend:     $24.80                                             ║
║                                                                      ║
║  [Delete Project]                                                    ║
╚══════════════════════════════════════════════════════════════════════╝
```

**Session card grouping within project detail:**
- Group by activity/lifecycle: Active (busy), Idle (running but idle), Stopped/Completed
- Use the existing `LiveSessionCard` component — no new card type
- Cards use the same responsive grid: `grid-cols-2 lg:grid-cols-3 xl:grid-cols-4`
- Section headers are lightweight: text-xs uppercase tracking-wider, with count badge

**Header navigation:**
- `[←]` back arrow — navigates to `/projects` list
- `[⚙]` gear — switches to project Settings tab
- `[+ New Session]` — opens `NewSessionDialog` with project pre-selected

**Tab implementation:**
- Reuse existing `Tabs`/`TabsList`/`TabsTrigger`/`TabsContent` from `ui/tabs.tsx`
- Use `variant="line"` (already supported) for underline-style tabs
- Default tab: Sessions (via URL hash or state)

---

### Screen 3: Fleet Dashboard (`/fleet`)

**Purpose:** Air traffic control. Cross-project operational view of all running sessions. For monitoring, not working.

```
╔══════════════════════════════════════════════════════════════════════╗
║  Header (56px)                                                       ║
║  ┌────────────────────────────────────────────────────────────────┐   ║
║  │  ⊞ Fleet                                     [+ New Session]   │   ║
║  │    47 sessions · 3 active · 2 idle · $12.40 today              │   ║
║  └────────────────────────────────────────────────────────────────┘   ║
╠══════════════════════════════════════════════════════════════════════╣
║  SummaryBar (existing, unchanged)                                    ║
║  ┌────────────────────────────────────────────────────────────────┐   ║
║  │  ⚡ 3 Active    ⏸ 2 Idle    # 24.5k Tokens   📋 0 Queued     │   ║
║  └────────────────────────────────────────────────────────────────┘   ║
║                                                                      ║
║  FleetToolbar (existing + "Project" GroupBy)                         ║
║  ┌────────────────────────────────────────────────────────────────┐   ║
║  │  [🔍 Search…]                                                  │   ║
║  │  Group: [Project ▾]  Sort: [Recent ▾]  Show: [Active ▾]       │   ║
║  └────────────────────────────────────────────────────────────────┘   ║
║                                                                      ║
║  ── Auth Rewrite (3) ─────────────────── [→ Open Project] ──────    ║
║  ┌──────────────┐ ┌──────────────┐ ┌──────────────┐                 ║
║  │ ● fix-login  │ │ ○ add-tests  │ │ ● refactor   │                 ║
║  │   working    │ │   idle       │ │   working    │                 ║
║  │   3.2k tok   │ │   1.1k tok   │ │   890 tok    │                 ║
║  └──────────────┘ └──────────────┘ └──────────────┘                 ║
║                                                                      ║
║  ── Q4 Campaign (1) ─────────────────── [→ Open Project] ──────    ║
║  ┌──────────────┐                                                   ║
║  │ ○ copy-draft │                                                   ║
║  │   idle       │                                                   ║
║  │   450 tok    │                                                   ║
║  └──────────────┘                                                   ║
║                                                                      ║
║  ── Scratch (2) ──────────────────────────────────  (muted) ────    ║
║  ┌──────────────┐ ┌──────────────┐                                  ║
║  │ ○ quick-fix  │ │ ■ one-off    │                                  ║
║  │   idle       │ │   stopped    │                                  ║
║  └──────────────┘ └──────────────┘                                  ║
╚══════════════════════════════════════════════════════════════════════╝
```

**Key changes from current Fleet page (`page.tsx`):**

1. **New "Project" GroupBy option** — added to `GroupBy` union type: `"project" | "directory" | "session-status" | "connection-status" | "source" | "none"`. Default changes from `"directory"` to `"project"`.

2. **Project group headers become navigable** — each group header includes a `[→ Open Project]` link that navigates to `/projects/:id`. This is the primary bridge from Fleet to focused project work.

3. **Session card links change** — when in project GroupBy mode, cards link to `/projects/:projectId/sessions/:sessionId`. In other modes, use flat `/sessions/:id` URLs for backwards compatibility.

4. **Scratch group styling** — slightly muted header text, positioned last in sort order.

5. **Everything else is preserved** — SummaryBar, other GroupBy modes, sort, search, retention filter, session card component.

---

### Screen 4: Data Sources (`/data-sources`)

**Purpose:** Manage external context providers. Connect GitHub, Notion, Linear. See what's global vs. project-specific.

```
╔══════════════════════════════════════════════════════════════════════╗
║  Header (56px)                                                       ║
║  ┌────────────────────────────────────────────────────────────────┐   ║
║  │  🔌 Data Sources                                               │   ║
║  │  Connect services that provide context for agent sessions      │   ║
║  └────────────────────────────────────────────────────────────────┘   ║
╠══════════════════════════════════════════════════════════════════════╣
║  Tabs (line variant)                                                 ║
║  ┌────────────────────────────────────────────────────────────────┐   ║
║  │  [Connected]  [Available]  [Workspace Roots]                   │   ║
║  └────────────────────────────────────────────────────────────────┘   ║
╠══════════════════════════════════════════════════════════════════════╣
║  Content — Connected Tab                                             ║
║                                                                      ║
║  ┌─ Source Card ──────────────────────────────────────────────────┐  ║
║  │  ┌──┐                                                          │  ║
║  │  │GH│  GitHub                          ✓ Connected             │  ║
║  │  └──┘  23 repos · Updated 5m ago                               │  ║
║  │                                                                │  ║
║  │  Scope: 🌐 Global (all projects)                              │  ║
║  │                                                                │  ║
║  │  [Browse Repos]    [Refresh]    [Disconnect]                   │  ║
║  └────────────────────────────────────────────────────────────────┘  ║
║                                                                      ║
║  ┌─ Source Card ──────────────────────────────────────────────────┐  ║
║  │  ┌──┐                                                          │  ║
║  │  │N │  Notion                          ✓ Connected             │  ║
║  │  └──┘  2 workspaces · 14 pages indexed                         │  ║
║  │                                                                │  ║
║  │  Scope: 📂 Auth Rewrite, 📂 Q4 Campaign                      │  ║
║  │                                                                │  ║
║  │  [Browse Pages]    [Manage]    [Disconnect]                    │  ║
║  └────────────────────────────────────────────────────────────────┘  ║
║                                                                      ║
║  (empty state if no sources connected)                               ║
║  "No data sources connected yet. Browse available integrations       ║
║   to connect your first source."                                     ║
║                                                                      ║
╠══════════════════════════════════════════════════════════════════════╣
║  Content — Available Tab                                             ║
║                                                                      ║
║  Grid of available integration cards (not yet connected)             ║
║  ┌────────────┐ ┌────────────┐ ┌────────────┐ ┌────────────┐       ║
║  │ [Sentry]   │ │ [Jira]     │ │ [Figma]    │ │ [Slack]    │       ║
║  │ Error      │ │ Issue      │ │ Design     │ │ Messaging  │       ║
║  │ tracking   │ │ tracking   │ │ files      │ │            │       ║
║  │ [Connect]  │ │ [Connect]  │ │ [Soon]     │ │ [Soon]     │       ║
║  └────────────┘ └────────────┘ └────────────┘ └────────────┘       ║
║                                                                      ║
╠══════════════════════════════════════════════════════════════════════╣
║  Content — Workspace Roots Tab                                       ║
║                                                                      ║
║  (relocated from Settings → Workspace Roots tab)                     ║
║  ┌────────────────────────────────────────────────────────────────┐  ║
║  │  ~/code              24 repos   [env]         [Scan] [Remove] │  ║
║  │  ~/personal/projects  3 repos   [user-added]  [Scan] [Remove] │  ║
║  │                                                                │  ║
║  │  [+ Add Root Directory]                                        │  ║
║  └────────────────────────────────────────────────────────────────┘  ║
╚══════════════════════════════════════════════════════════════════════╝
```

**Source Card Anatomy:**
- Logo badge (32×32, rounded, bg-card)
- Service name (font-semibold text-sm) + status indicator (✓ Connected / ✗ Error)
- Metadata line (text-xs text-muted-foreground): repo count, page count, last sync
- Scope line: 🌐 Global or 📂 Project1, 📂 Project2 (clickable project links)
- Action buttons: Browse (primary ghost), service-specific action, Disconnect (destructive ghost)

---

## Part 2: Visual Design Direction

### 2.1 Density

**Principle:** Desktop-first, information-dense. This is a professional tool, not a consumer app.

| Token | Value | Usage |
|---|---|---|
| Page padding | `p-3 sm:p-4 lg:p-6` | Consistent with current responsive padding |
| Card gap | `gap-3 sm:gap-4` | Grid gaps between session/project cards |
| Section gap | `space-y-4 sm:space-y-6` | Between major sections (SummaryBar → Toolbar → Content) |
| Inner card padding | `px-4 py-3` (header), `px-4 pb-4` (content) | Preserve current `LiveSessionCard` density |
| List item height | `py-1.5` (sidebar), `py-2` (content lists) | Compact enough for 20+ items visible |
| Touch targets | `pointer-coarse:h-9 pointer-coarse:w-9` | Existing pattern — enlarge on touch devices only |

**Density tiers (not implemented now, future):**
- Compact: reduce paddings by 25%, text-[11px] for metadata
- Default: current sizes (described above)
- Comfortable: increase paddings by 25%, text-sm for metadata

### 2.2 Typography

**Font stack (unchanged):**
```
--font-sans: Inter, "Helvetica Neue", Arial, sans-serif
--font-mono: "JetBrains Mono", "Fira Code", "Cascadia Code", ui-monospace, monospace
```

**Type scale for the redesigned screens:**

| Role | Size | Weight | Font | Example |
|---|---|---|---|---|
| Page title | `text-base sm:text-lg` | `font-semibold` | Sans | "Agent Fleet", "Auth Rewrite" |
| Page subtitle | `text-xs` | normal | Sans | "5 sessions · 2 active · $4.23" |
| Section header | `text-xs` | `font-semibold uppercase tracking-wider` | Sans | "ACTIVE (2)" |
| Card title | `text-sm` | `font-semibold` | Mono | "fix-login" |
| Card metadata | `text-xs` | normal | Sans | "3.2k tok · $0.42" |
| Badge text | `text-[10px]` | normal | Sans | "working", "conductor" |
| Sidebar item | `text-xs` | `font-medium` | Sans | Project and session names |
| Timestamp | `text-xs` | normal | Sans | "2m ago" |
| Status pip | `text-[10px]` | normal | Mono | "idle" |
| Cost figure | `text-xs` | normal | Mono | "$4.23" |

**Mono vs. Sans rule:** Session titles (user-generated, often code-like: "fix-login", "add-tests") use mono. Project names (human-created: "Auth Rewrite") use sans. IDs and costs always mono.

### 2.3 Navigation

**Updated icon rail (48px):**

```
┌──────┐
│ LOGO │  ← Click = last active project, or /projects if none
├──────┤
│  ⊞   │  ← Fleet (/fleet) — cross-project dashboard
│  📁  │  ← Projects — sidebar panel with project tree
│  🔌  │  ← Data Sources (/data-sources) — link, panel optional
├──────┤
│      │  ← Spacer
│ {P}  │  ← Plugin-contributed items (GitHub, etc.)
├──────┤
│  📊  │  ← Analytics (/analytics) — link, no panel
│  ⚙️  │  ← Settings (/settings) — link, no panel
│ prof │  ← Profile badge (if non-default)
│ v1   │  ← Version
└──────┘
```

**Changes from current:**
- "Projects" icon (📁 FolderOpen) replaces "Repositories" (FolderGit2)
- Repositories icon is removed from rail — content moves to Data Sources → Workspace Roots
- Plugin items stay in their current position (dynamic, between core and bottom)
- Logo click target changes from "welcome" to "last active project"

**Sidebar panel mapping:**

| Rail item | Panel | Panel content |
|---|---|---|
| Fleet | FleetPanel | Live session list (compact), filterable |
| Projects | ProjectsPanel | Project tree (current FleetPanel, renamed) |
| Data Sources | DataSourcesPanel (optional) | Compact list of connected sources |

**Navigation hierarchy:**
```
/projects                    → Projects list page
/projects/:id                → Project detail (Sessions tab)
/projects/:id/sessions/:sid  → Session detail (project-scoped)
/fleet                       → Fleet dashboard
/data-sources                → Data Sources page
/sessions/:id                → Session detail (flat, backwards-compat redirect)
/analytics                   → Analytics (unchanged)
/settings                    → Settings (reorganized)
```

### 2.4 Card Hierarchy

Three tiers of cards in the system, with visual weight proportional to information density:

**Tier 1: Session Card (LiveSessionCard — existing)**
- Background: `bg-card` with `border`
- Hover: `hover:border-foreground/20 hover:shadow-md`
- Active session: full opacity, green status dot with `animate-pulse`
- Inactive session: `opacity-60`
- Content: title (mono), status badge, token/cost, timestamp, action buttons
- Size: flexible within grid column (min ~180px, max ~320px)

**Tier 2: Project Card (new, on /projects page)**
- Background: `bg-card` with `border` — same base as session card
- Hover: `hover:border-foreground/20 hover:shadow-md`
- Distinguisher: no status dot; instead, a row of status pips (tiny dots representing session statuses)
- Content: project name (sans), session count, pip row, last activity, cost
- Size: larger than session cards — designed for fewer on screen (~3-4 per row)
- Scratch variant: `bg-muted/50` background, notepad icon, always full-width row at top

**Tier 3: Source Card (on /data-sources page)**
- Background: `bg-card` with `border` — same base
- Hover: lighter hover (no shadow-md, just `hover:border-foreground/10`)
- Distinguisher: integration logo badge (32×32), scope indicators
- Content: service name, connection status, metadata, scope, action buttons
- Size: full-width list items (not grid cards) — stacked vertically
- Available (unconnected) variant: `border-dashed` or `bg-muted/30`, muted text

### 2.5 Status Language

Standardize the vocabulary for session and connection states across all screens:

**Session activity status (what the agent is doing):**

| Status | Dot | Badge text | Color |
|---|---|---|---|
| Working | `●` (filled, pulsing) | "working" | `green-500 animate-pulse` |
| Idle | `○` (filled, static) | "idle" | `muted-foreground/50` |
| Waiting | `◐` (half) | "waiting" | `amber-500` |

**Session lifecycle status (terminal states):**

| Status | Icon | Badge text | Color |
|---|---|---|---|
| Stopped | `■` (square) | "stopped" | `muted-foreground` |
| Completed | `✓` (check) | "completed" | `muted-foreground` |
| Disconnected | `⚡` (wifi-off) | "disconnected" | `red-400` |
| Error | `✗` (x) | "error" | `destructive` |

**Connection status (integration health):**

| Status | Indicator | Color |
|---|---|---|
| Connected | `✓ Connected` | `green-500` |
| Disconnected | `✗ Disconnected` | `muted-foreground` |
| Error | `⚠ Error` | `destructive` |

**Session count pips (on project cards):**
- `●` green-500 = active/working
- `○` muted-foreground = idle
- `■` muted-foreground/40 = stopped/completed
- Max 8 pips shown, then `+N` text after

### 2.6 Empty States

Each screen has a purposeful empty state that guides the user:

**Projects List (no projects):**
```
┌──────────────────────────────────────────┐
│            📁                            │
│                                          │
│   No projects yet                        │
│                                          │
│   Projects organize related AI sessions  │
│   into focused workstreams.              │
│                                          │
│   [+ Create Project]                     │
│                                          │
│   Or start an ad-hoc session in Scratch  │
│   [→ Open Scratch]                       │
└──────────────────────────────────────────┘
```

**Project Detail — Sessions tab (no sessions in project):**
```
┌──────────────────────────────────────────┐
│            💬                            │
│                                          │
│   No sessions in this project            │
│                                          │
│   Start your first AI session to begin   │
│   working on Auth Rewrite.               │
│                                          │
│   [+ New Session]                        │
└──────────────────────────────────────────┘
```

**Project Detail — Data Sources tab (no attached sources):**
```
┌──────────────────────────────────────────┐
│            🔗                            │
│                                          │
│   No data sources attached               │
│                                          │
│   Attach GitHub repos, Notion pages, or  │
│   other sources to provide context for   │
│   sessions in this project.              │
│                                          │
│   [+ Attach Data Source]                 │
│                                          │
│   Global sources (workspace roots) are   │
│   always available.                      │
└──────────────────────────────────────────┘
```

**Data Sources — Connected tab (nothing connected):**
```
┌──────────────────────────────────────────┐
│            🔌                            │
│                                          │
│   No data sources connected              │
│                                          │
│   Connect external services to provide   │
│   context for your AI sessions.          │
│                                          │
│   [Browse Available Sources →]           │
│   (switches to Available tab)            │
└──────────────────────────────────────────┘
```

**Fleet Dashboard (no sessions at all):**
Already exists: `"No sessions running. Click 'New Session' to start a new agent session."` — keep as-is but add a secondary CTA: `[→ Go to Projects]`.

**Design tokens for empty states:**
- Container: `flex flex-col items-center justify-center text-center gap-3 py-12`
- Icon: 48×48, `text-muted-foreground/30`
- Heading: `text-sm font-medium text-foreground`
- Body: `text-xs text-muted-foreground leading-relaxed max-w-xs`
- CTA: `Button size="sm"` with gradient or primary variant

---

## Part 3: Phased Implementation Plan

### Phase 0: Foundation (No UI Changes)

Lays the groundwork — route scaffolding, type changes, GroupBy addition. Users see no visible changes.

- [ ] 1. Add "project" to FleetToolbar GroupBy
  **What**: Add `"project"` to the `GroupBy` union type in `fleet-toolbar.tsx`. Add label entry in `GROUP_BY_LABELS`. Add grouping logic in `page.tsx` that groups `searchFiltered` sessions by `projectId`/`projectName`. Default remains `"directory"` for now.
  **Files**: `client/src/components/fleet/fleet-toolbar.tsx`, `client/src/app/page.tsx`
  **Acceptance**: GroupBy dropdown shows "Project" option. Selecting it groups sessions by project name with Scratch/unassigned at the bottom.

- [ ] 2. Register `/projects` and `/projects/:id` routes
  **What**: Add lazy-loaded route entries in `app.tsx` for `ProjectsPage` and `ProjectDetailPage`. Create minimal placeholder page components (title + "Coming soon") so routes resolve without 404. Add `/projects/:id/sessions/:sid` route that renders `SessionDetailPage` with project context.
  **Files**: `client/src/app.tsx`, `client/src/app/projects/page.tsx` (new), `client/src/app/projects/[id]/page.tsx` (new)
  **Acceptance**: Navigating to `/projects` and `/projects/some-id` renders placeholder pages within the existing layout.

- [ ] 3. Register `/fleet` route alias
  **What**: Add `/fleet` as a route that renders the existing `FleetPage` component (same as `/`). Both `/` and `/fleet` render the same page. Update `isFleetRoute()` in `sidebar-icon-rail.tsx` to include `/fleet`.
  **Files**: `client/src/app.tsx`, `client/src/components/layout/sidebar-icon-rail.tsx`
  **Acceptance**: `/fleet` renders the Fleet dashboard identically to `/`.

- [ ] 4. Register `/data-sources` route
  **What**: Add lazy-loaded route for `DataSourcesPage`. Create minimal placeholder page.
  **Files**: `client/src/app.tsx`, `client/src/app/data-sources/page.tsx` (new)
  **Acceptance**: `/data-sources` renders within layout.

### Phase 1: Projects List Page

The first visible change — users can see their projects as a dedicated page.

- [ ] 5. Build ProjectCard component
  **What**: New component rendering a project as a card with name, session count, status pips, last activity, cost. Uses `Card` from ui. Accepts `ProjectResponse` + derived summary data. Scratch variant with `bg-muted/50` and notepad icon.
  **Files**: `client/src/components/fleet/project-card.tsx` (new)
  **Acceptance**: Component renders correctly with mock data in isolation (Storybook or test page).

- [ ] 6. Build ProjectsPage with real data
  **What**: Full `/projects` page. Fetches projects via `useProjects()`, derives per-project session counts from `useSessionsContext()`, renders Scratch card (full-width, pinned top) + project cards grid. Search bar, sort dropdown (Recent Activity, Name, Most Sessions). Empty state when no user projects exist. `[+ Project]` button opens `CreateProjectDialog`.
  **Files**: `client/src/app/projects/page.tsx`
  **Acceptance**: Page shows real projects and sessions. Creating a project refreshes the list. Scratch appears first.

- [ ] 7. Add "Projects" to icon rail
  **What**: Add `FolderOpen` icon button for "projects" view in `sidebar-icon-rail.tsx`. Wire it to navigate to `/projects`. Add `"projects"` to sidebar view system. The existing FleetPanel becomes the Projects panel (rename label from "Fleet" to "Projects" in the panel header). Update `viewForPathname` to map `/projects*` → `"projects"`.
  **Files**: `client/src/components/layout/sidebar-icon-rail.tsx`, `client/src/contexts/sidebar-context.tsx`, `client/src/components/layout/fleet-panel.tsx`, `client/src/components/layout/sidebar-panel.tsx`
  **Acceptance**: "Projects" icon appears in rail between Fleet and plugin items. Clicking shows the projects panel (existing tree). Route syncs correctly.

### Phase 2: Project Detail Page

The 80%-time screen. Users can work within a single project.

- [ ] 8. Build ProjectDetailPage — Sessions tab
  **What**: Full `/projects/:id` page. Fetches project by ID, filters sessions by `projectId`. Header shows project name, session count, cost. Tabs: Sessions (default), Data Sources, Activity, Settings. Sessions tab groups cards into Active/Idle/Stopped sections using the same `LiveSessionCard`. Back arrow navigates to `/projects`. `[+ New Session]` pre-selects the project.
  **Files**: `client/src/app/projects/[id]/page.tsx`, `client/src/hooks/use-project.ts` (new, single project fetch)
  **Acceptance**: Clicking a project card from `/projects` navigates to detail page. Sessions tab shows that project's sessions grouped by status.

- [ ] 9. Project-scoped session URLs
  **What**: Session cards within `/projects/:id` link to `/projects/:projectId/sessions/:sessionId`. The session detail route at this path renders the existing `SessionDetailPage` with a breadcrumb back to the project. The old `/sessions/:id` route continues to work (no redirect yet).
  **Files**: `client/src/app/projects/[id]/sessions/[sid]/page.tsx` (new, thin wrapper), `client/src/components/fleet/live-session-card.tsx` (add optional `projectId` prop for link construction)
  **Acceptance**: Clicking a session card in project detail navigates to `/projects/:id/sessions/:sid`. Back button returns to project. Direct `/sessions/:id` still works.

- [ ] 10. Project Detail — Data Sources tab (placeholder)
  **What**: Stub tab showing "Data source attachment coming soon" message with a link to `/data-sources` for global source management. This tab will be fully implemented when the backend supports per-project source attachment.
  **Files**: `client/src/app/projects/[id]/page.tsx` (within existing file)
  **Acceptance**: Data Sources tab renders placeholder content. No backend changes needed.

- [ ] 11. Project Detail — Activity tab
  **What**: Project-scoped activity feed. Filters the session events from `useSessionsContext` by project, renders a compact timeline. Each entry: relative timestamp, session title badge, event type description. Uses existing event data — no new API calls needed.
  **Files**: `client/src/app/projects/[id]/page.tsx` (within existing file), `client/src/components/fleet/project-activity-feed.tsx` (new)
  **Acceptance**: Activity tab shows recent events from all sessions in the project, sorted newest-first.

- [ ] 12. Project Detail — Settings tab
  **What**: Project metadata view/edit. Shows name (InlineEdit), description (textarea), creation date, total sessions, total spend. Delete button with `ConfirmDeleteProjectDialog`. Uses existing `useUpdateProject` and `useDeleteProject` hooks.
  **Files**: `client/src/app/projects/[id]/page.tsx` (within existing file)
  **Acceptance**: Can rename project, edit description, delete project from detail page.

### Phase 3: Fleet Refinements

Make Fleet feel like the "telescope view" — cross-project operational dashboard.

- [ ] 13. Default GroupBy to "Project"
  **What**: Change `DEFAULT_PREFS` in `fleet-toolbar.tsx` from `{ groupBy: "directory" }` to `{ groupBy: "project" }`. Clear stale localStorage prefs on first load (one-time migration). Project group headers become clickable links to `/projects/:id`.
  **Files**: `client/src/components/fleet/fleet-toolbar.tsx`, `client/src/app/page.tsx`
  **Acceptance**: Fresh installs show Fleet grouped by Project. Group headers link to project detail pages.

- [ ] 14. Move Fleet route from `/` to `/fleet`
  **What**: Change the canonical Fleet route to `/fleet`. Make `/` redirect to either `/fleet` (if user has no projects) or `/projects` (if user has projects). Preserve `/` as working URL via redirect, not a hard break. Update all internal links.
  **Files**: `client/src/app.tsx`, `client/src/components/layout/sidebar-icon-rail.tsx`, `client/src/components/layout/fleet-panel.tsx`, `client/src/components/layout/header.tsx`
  **Acceptance**: `/fleet` is the canonical URL. `/` redirects intelligently. All internal navigation works.

- [ ] 15. Update logo click behavior
  **What**: Logo click navigates to last active project (stored in `usePersistedState`) or `/projects` if no project has been visited. This replaces the current "welcome" view toggle. Welcome page remains accessible via URL but is no longer a sidebar view.
  **Files**: `client/src/components/layout/sidebar-icon-rail.tsx`
  **Acceptance**: Logo click goes to project, not welcome screen.

### Phase 4: Data Sources Page

Unify integrations and workspace roots under one roof.

- [ ] 16. Build DataSourcesPage — Connected tab
  **What**: Full `/data-sources` page with Connected tab. Lists currently connected integrations using data from `useIntegrationsContext()`. Each integration renders as a Source Card (logo, name, status, metadata, scope, actions). Actions: Browse (navigates to integration's browse route), Disconnect. Empty state guides to Available tab.
  **Files**: `client/src/app/data-sources/page.tsx`
  **Acceptance**: Connected integrations appear with correct status. Browse navigates to existing integration browse routes.

- [ ] 17. Data Sources — Available tab
  **What**: Shows integrations from `getIntegrations()` that are NOT connected. Grid of cards with service name, description, [Connect] button (links to existing connection flow). "Coming soon" badge for future integrations.
  **Files**: `client/src/app/data-sources/page.tsx` (within existing file)
  **Acceptance**: Available tab shows unconnected sources. Connect button starts existing OAuth/device flow.

- [ ] 18. Data Sources — Workspace Roots tab
  **What**: Relocate workspace roots management from `Settings → Workspace Roots` to `Data Sources → Workspace Roots`. Reuse existing `WorkspaceRootsTab` component with minor layout adjustments. Keep the Settings tab as a redirect/link to `/data-sources?tab=roots`.
  **Files**: `client/src/app/data-sources/page.tsx`, `client/src/app/settings/page.tsx`
  **Acceptance**: Workspace roots are manageable from Data Sources page. Settings redirects or links appropriately.

- [ ] 19. Add Data Sources to icon rail
  **What**: Replace "Repositories" icon with "Data Sources" (Plug icon). Update `CORE_VIEW_DEFAULT_ROUTE` to include `"data-sources": "/data-sources"`. Remove the "repositories" icon rail button. Optionally add a compact data sources panel showing connected source names and status dots.
  **Files**: `client/src/components/layout/sidebar-icon-rail.tsx`, `client/src/contexts/sidebar-context.tsx`
  **Acceptance**: "Data Sources" icon appears in rail where "Repositories" was. Clicking navigates to `/data-sources`.

### Phase 5: Navigation Polish

Final cleanup — remove vestiges of old navigation, ensure everything is cohesive.

- [ ] 20. Remove standalone Repositories page
  **What**: Remove `/repositories` and `/repositories/:path` routes. Repository browsing is now available through Data Sources → Workspace Roots → individual repo links (or through the existing integration browse routes). Update any internal links pointing to `/repositories`.
  **Files**: `client/src/app.tsx`, `client/src/components/layout/sidebar-icon-rail.tsx`
  **Acceptance**: `/repositories` returns 404 (or redirects to `/data-sources?tab=roots`). No broken internal links.

- [ ] 21. Reorganize Settings tabs
  **What**: Group remaining settings into two conceptual sections. Remove Integrations and Workspace Roots tabs (moved to Data Sources). Reorder remaining tabs: Skills, Agents, Providers, Harnesses | Appearance, Keybindings, API Keys, About.
  **Files**: `client/src/app/settings/page.tsx`
  **Acceptance**: Settings page has 6-8 tabs (down from 10). No orphaned tabs. Integrations tab either removed or shows "Moved to Data Sources" link.

- [ ] 22. Flat `/sessions/:id` redirect
  **What**: When navigating to `/sessions/:id`, look up the session's `projectId`. If found, redirect to `/projects/:projectId/sessions/:id`. If not (or scratch), render inline. This makes old bookmarks and links continue working.
  **Files**: `client/src/app/sessions/[id]/_page-client.tsx`
  **Acceptance**: Old session URLs redirect to project-scoped URLs. Bookmarks and shared links don't break.

- [ ] 23. Command palette updates
  **What**: Add "Go to Projects" and "Go to Data Sources" commands to the command palette. Update "Go to Fleet" to target `/fleet`. Update any command that references old routes.
  **Files**: `client/src/components/commands/navigation-commands.tsx`
  **Acceptance**: Command palette includes all new navigation targets.

---

## Objectives

### Core Objective
Transform Weave Fleet's navigation model from session-centric (flat Fleet page) to project-centric (projects as primary anchor, Fleet as operational dashboard) while preserving every existing component and interaction pattern.

### Deliverables
- [ ] Mid-fidelity wireframe specifications for 4 screens (Projects list, Project detail, Fleet, Data Sources)
- [ ] Visual design direction covering density, typography, navigation, card hierarchy, status language, empty states
- [ ] Phased implementation plan with 23 concrete tasks across 6 phases

### Definition of Done
- [ ] `/projects` page shows all projects with session summaries
- [ ] `/projects/:id` shows project detail with sessions, data sources, activity, settings tabs
- [ ] Fleet page supports "Project" grouping (default) with clickable project headers
- [ ] Data Sources page unifies integrations and workspace roots
- [ ] Icon rail reflects new navigation: Fleet, Projects, Data Sources
- [ ] All existing URLs continue to work (redirects where needed)
- [ ] All existing components reused (no rewrites of LiveSessionCard, SessionGroup, etc.)
- [ ] `dotnet build -c Release` succeeds (backend unchanged)
- [ ] `npm run build` succeeds in `client/`

### Guardrails (Must NOT)
- Must NOT rewrite `LiveSessionCard`, `SessionGroup`, `FleetToolbar`, or `SummaryBar`
- Must NOT break existing `/sessions/:id` URLs (redirect is OK)
- Must NOT require backend API changes in Phase 0–2 (client-side data derivation only)
- Must NOT remove the Welcome page (just de-emphasize from primary nav)
- Must NOT change the plugin contribution model
- Must NOT break mobile/responsive behavior
- Must NOT alter the theme system or color tokens

## Verification

- [ ] All existing Vitest tests pass (`npm run test` in `client/`)
- [ ] New pages render correctly at 1440w, 768w, and 360w viewpoints
- [ ] Keyboard navigation works: Tab through icon rail, Enter to select, arrow keys in project tree
- [ ] Screen reader: all new pages have appropriate `aria-label`, `role`, heading hierarchy
- [ ] No console errors on any new route
- [ ] `/sessions/:id` still renders correctly (backwards compat)
- [ ] Fleet page with "Project" grouping handles: 0 projects, 1 project, 10+ projects, sessions without projectId
- [ ] Empty states render correctly on each new page
