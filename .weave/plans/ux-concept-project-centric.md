# UX Concept: Project-Centric Navigation Model

## TL;DR
> **Summary**: A concrete UX concept that reorganizes Weave Fleet around Projects as the primary navigation unit, Fleet as a cross-project operational dashboard, and Plugins/Data Sources as contextual providers — with Scratch as the default ad hoc project. Includes wireframes for the three most important screens.
> **Estimated Effort**: N/A (design concept — no code changes)

## Context

### Original Request
Create a realistic UX concept for Weave Fleet based on the model: Projects are primary; Fleet is a cross-project operational view; Plugins/Data Sources provide context and can be attached globally or at the project level; Scratch is the default ad hoc project.

### Key Findings (Current State)

**Current navigation structure:**
- Icon rail with: Weave logo (Welcome), Fleet (LayoutGrid), GitHub (plugin-contributed), Repositories (FolderGit2), then bottom links for Analytics and Settings
- Fleet panel shows a flat project tree in the sidebar (projects → sessions), with a "Fleet" link at the top
- Main area at `/` is the "Agent Fleet" page — a cross-project session grid with summary bar, toolbar (group/sort/filter), and live session cards
- Session detail at `/sessions/{id}` — activity stream with messages, tool calls, delegation cards
- GitHub gets its own sidebar panel and routes (`/github`, `/github/:owner/:repo`)
- Settings is a full page with horizontal tabs: Skills, Agents, Providers, Keybindings, Appearance, Harnesses, Integrations, Workspace Roots, API Keys, About
- Analytics is a full page with tabs: Overview, Projects, Sessions, Models

**Current layout pattern:**
- `Sidebar = IconRail (48px) + ContextualPanel (resizable 180–480px)`
- `Main = Header (56px) + scrollable content`
- Views toggle via icon rail; some views have panels (fleet, github, repositories), some don't (analytics, settings)

**What works well:**
- Icon rail + panel model is clean and extensible
- Session cards with status dots, badges, and live updates feel responsive
- Plugin contribution model is well-designed (sidebar items, panels, routes, settings sections)
- The fleet toolbar (group/sort/filter/search) is already mature

**What the redesign should address:**
- Projects should feel like the primary navigational anchor, not just sidebar groupings
- Fleet (operational dashboard) should be the cross-project "control room," not a project
- Plugins/Data Sources need a clearer attachment model (global vs. per-project)
- The "Scratch" project should feel intentional, not like ungrouped sessions
- Repositories view is an outlier — it should fold into the source/plugin model
- Settings has 10+ tabs that could be organized better

---

## 1. Top-Level Navigation

### Icon Rail (left, 48px, always visible)

```
┌──────┐
│ LOGO │  ← Brand/home. Click = go to last project or Scratch
├──────┤
│  ⊞   │  ← Fleet (cross-project operational dashboard)
│  📁  │  ← Projects (project list + switcher)
│  🔌  │  ← Data Sources (global plugin/integration management)
├──────┤
│      │  ← Spacer
│  📊  │  ← Analytics (link, no panel)
│  ⚙️  │  ← Settings (link, no panel)
│ v1   │  ← Version badge
└──────┘
```

**Changes from current:**
- "Fleet" moves from being the default/home to being the explicit operational view
- "Projects" replaces the fleet panel as the primary sidebar navigation
- "Repositories" icon is removed — repositories are a data source/session source
- "GitHub" icon is removed from core rail — it becomes a data source with optional project attachment
- "Data Sources" is new — combines Integrations + Workspace Roots into a unified "external context" view
- Logo click goes to last-active project (or Scratch), not Fleet

### Panel Views (adjacent to icon rail)

| Icon Rail Item | Has Panel? | Panel Content |
|---|---|---|
| Fleet | Yes | Live session list (compact), sortable, filterable — quick-switch to any running session across projects |
| Projects | Yes | Project tree: Scratch at top, then user projects. Each project expands to show sessions. Active project highlighted. |
| Data Sources | Yes | Connected sources list: GitHub, Notion, workspace roots. Quick status indicators. |
| Analytics | No | Full page |
| Settings | No | Full page |

---

## 2. Primary Screens and Their Purpose

### 2.1 Project Detail (`/projects/{id}`) — **Primary workspace**
The screen you spend 80% of your time on. Shows everything about one project: its sessions, attached data sources, and project-level context.

**Purpose:** Focus on a single body of work. Create sessions, see active/recent sessions, manage project-specific data source attachments.

### 2.2 Fleet Dashboard (`/fleet` or `/`) — **Operational control room**
Cross-project view of all running/recent sessions. This is the "air traffic control" view.

**Purpose:** Monitor all active agent sessions across projects. Quickly spot stuck sessions, high token usage, or idle agents. Bulk actions. No project context — pure operations.

### 2.3 Session Detail (`/projects/{projectId}/sessions/{sessionId}`) — **Session interaction**
The conversation view. Activity stream, prompt input, tool calls, delegation cards.

**Purpose:** Interact with a specific agent session. Deeply focused — one conversation at a time.

### 2.4 Data Sources (`/data-sources`) — **Integration management**
Global data source configuration. Connect GitHub, Notion, Linear, workspace roots. See what's connected, manage credentials, configure global vs. project-level attachment.

**Purpose:** Manage external context providers. This is where you connect services and configure what data they make available to sessions.

### 2.5 Analytics (`/analytics`) — **Usage and insights** (unchanged)
Token usage, cost tracking, session statistics. Filterable by project, date range, model.

### 2.6 Settings (`/settings`) — **Configuration** (reorganized)
Streamlined into fewer, clearer sections.

### 2.7 Scratch Project — **Ad hoc sessions**
Scratch is a real project that always exists. It's pinned at the top of the project list. Sessions created without selecting a project go here. It has its own project detail view. It cannot be deleted or renamed.

---

## 3. Recommended Layout: Project Detail Screen

### URL: `/projects/{id}`

```
┌─────────────────────────────────────────────────────────────────────┐
│ Header                                                              │
│ ┌───────────────────────────────────────────────────────────────┐   │
│ │ [Project Icon] Auth Rewrite    12 sessions · $4.23 total     │   │
│ │ [+ New Session]  [⚙ Project Settings]                        │   │
│ └───────────────────────────────────────────────────────────────┘   │
├─────────────────────────────────────────────────────────────────────┤
│ Content                                                             │
│                                                                     │
│ ┌─ Tabs ──────────────────────────────────────────────────────────┐ │
│ │ [Sessions]  [Data Sources]  [Activity]                         │ │
│ └────────────────────────────────────────────────────────────────-┘ │
│                                                                     │
│ ══ Sessions Tab (default) ═══════════════════════════════════════   │
│                                                                     │
│  ┌─ Active Sessions ────────────────────────────────────────────┐   │
│  │ ┌──────────────┐ ┌──────────────┐ ┌──────────────┐          │   │
│  │ │ ● fix-login  │ │ ○ add-tests  │ │ ● refactor   │          │   │
│  │ │   working    │ │   idle       │ │   working    │          │   │
│  │ │   3.2k tok   │ │   1.1k tok   │ │   890 tok    │          │   │
│  │ │   2m ago     │ │   14m ago    │ │   <1m ago    │          │   │
│  │ └──────────────┘ └──────────────┘ └──────────────┘          │   │
│  └──────────────────────────────────────────────────────────────┘   │
│                                                                     │
│  ┌─ Recent Sessions ───────────────────────────────────────────-┐   │
│  │ ┌──────────────┐ ┌──────────────┐                            │   │
│  │ │ ■ auth-flow  │ │ ■ db-schema  │                            │   │
│  │ │   stopped    │ │   completed  │                            │   │
│  │ │   12k tok    │ │   8.4k tok   │                            │   │
│  │ │   2h ago     │ │   yesterday  │                            │   │
│  │ └──────────────┘ └──────────────┘                            │   │
│  └──────────────────────────────────────────────────────────────┘   │
│                                                                     │
│ ══ Data Sources Tab ═════════════════════════════════════════════   │
│                                                                     │
│  Attached to this project:                                          │
│  ┌────────────────────────────────────────────────────────────────┐ │
│  │ [GitHub] weave-dev/auth-service  ✓ Connected  [Detach]        │ │
│  │ [Notion] Auth Rewrite Spec page  ✓ Connected  [Detach]        │ │
│  │                                                                │ │
│  │ [+ Attach Data Source]                                         │ │
│  │   GitHub repo, Notion page, Linear project, local directory…   │ │
│  └────────────────────────────────────────────────────────────────┘ │
│                                                                     │
│  Inherited from global:                                             │
│  ┌────────────────────────────────────────────────────────────────┐ │
│  │ [Workspace] ~/code  (global root)                              │ │
│  └────────────────────────────────────────────────────────────────┘ │
│                                                                     │
│ ══ Activity Tab ═════════════════════════════════════════════════   │
│                                                                     │
│  Project-wide activity feed — recent session events across all      │
│  sessions in this project. Compact timeline view.                   │
│                                                                     │
└─────────────────────────────────────────────────────────────────────┘
```

### Key Design Decisions — Project Detail

1. **Sessions tab is default.** The project is about its sessions. Active sessions appear first with visual prominence (larger cards, status animations). Recent/stopped sessions below in a quieter treatment.

2. **Data Sources tab** shows what external context is attached to *this project*. Users can attach a GitHub repo, Notion page, or Linear project directly to the project. These become available as default context when creating new sessions in this project.

3. **Activity tab** is a project-wide event timeline — useful for seeing what's happening across multiple sessions without opening each one.

4. **Session cards** are the same `LiveSessionCard` component used in Fleet, but here they're scoped to one project and split into Active vs. Recent.

5. **"+ New Session"** in the header pre-selects this project and offers attached data sources as default context.

---

## 4. Recommended Layout: Fleet Screen

### URL: `/fleet` (or `/`)

```
┌─────────────────────────────────────────────────────────────────────┐
│ Header                                                              │
│ ┌───────────────────────────────────────────────────────────────┐   │
│ │ ⊞ Fleet                                                      │   │
│ │ 47 sessions · 3 active · 2 idle · $12.40 today               │   │
│ │ [+ New Session]                                               │   │
│ └───────────────────────────────────────────────────────────────┘   │
├─────────────────────────────────────────────────────────────────────┤
│ Content                                                             │
│                                                                     │
│  ┌─ Summary Bar ────────────────────────────────────────────────┐   │
│  │  ⚡ 3 Active    ⏸ 2 Idle    # 24.5k Tokens   📋 0 Queued   │   │
│  └──────────────────────────────────────────────────────────────┘   │
│                                                                     │
│  ┌─ Toolbar ────────────────────────────────────────────────────┐   │
│  │  [🔍 Search sessions…]                                      │   │
│  │  Group: [Project ▾]  Sort: [Recent ▾]  Show: [Active ▾]     │   │
│  └──────────────────────────────────────────────────────────────┘   │
│                                                                     │
│  ┌─ Auth Rewrite (3 sessions) ──────────────────────────────────┐   │
│  │ ┌──────────────┐ ┌──────────────┐ ┌──────────────┐          │   │
│  │ │ ● fix-login  │ │ ○ add-tests  │ │ ● refactor   │          │   │
│  │ │   working    │ │   idle       │ │   working    │          │   │
│  │ │   3.2k tok   │ │   1.1k tok   │ │   890 tok    │          │   │
│  │ └──────────────┘ └──────────────┘ └──────────────┘          │   │
│  └──────────────────────────────────────────────────────────────┘   │
│                                                                     │
│  ┌─ Q4 Campaign (1 session) ────────────────────────────────────┐   │
│  │ ┌──────────────┐                                             │   │
│  │ │ ○ copy-draft │                                             │   │
│  │ │   idle       │                                             │   │
│  │ │   450 tok    │                                             │   │
│  │ └──────────────┘                                             │   │
│  └──────────────────────────────────────────────────────────────┘   │
│                                                                     │
│  ┌─ Scratch (2 sessions) ───────────────────────────────────────┐   │
│  │ ┌──────────────┐ ┌──────────────┐                            │   │
│  │ │ ○ quick-fix  │ │ ■ one-off    │                            │   │
│  │ │   idle       │ │   stopped    │                            │   │
│  │ └──────────────┘ └──────────────┘                            │   │
│  └──────────────────────────────────────────────────────────────┘   │
│                                                                     │
└─────────────────────────────────────────────────────────────────────┘
```

### Key Design Decisions — Fleet

1. **Fleet is the operational dashboard, not the home screen.** Users who live in one project should go straight to their project. Fleet is for people managing work across multiple projects.

2. **Default grouping is "Project"** (changed from "Directory"). This aligns with the project-centric model. Directory grouping remains available as an option.

3. **Group-by options become:** Project (default), Directory, Session Status, Connection Status, Source, None — "Project" is added as the new default, "Directory" is preserved.

4. **Session cards link to project-scoped URLs:** `/projects/{projectId}/sessions/{sessionId}` instead of `/sessions/{id}`. This maintains project context in the URL.

5. **"Scratch" group** is always last (or pinned at bottom) since it's the catch-all. It uses a distinct subtle style (e.g., slightly muted header) to signal "these are ad hoc."

6. **Project group headers are clickable** — clicking "Auth Rewrite" navigates to `/projects/{id}`, giving quick access from Fleet to the focused project view.

---

## 5. Recommended Layout: Data Sources (Plugins)

### URL: `/data-sources`

```
┌─────────────────────────────────────────────────────────────────────┐
│ Header                                                              │
│ ┌───────────────────────────────────────────────────────────────┐   │
│ │ 🔌 Data Sources                                              │   │
│ │ Connect external services to provide context for sessions     │   │
│ └───────────────────────────────────────────────────────────────┘   │
├─────────────────────────────────────────────────────────────────────┤
│ Content                                                             │
│                                                                     │
│ ┌─ Tabs ──────────────────────────────────────────────────────────┐ │
│ │ [Connected]  [Available]  [Workspace Roots]                    │ │
│ └────────────────────────────────────────────────────────────────-┘ │
│                                                                     │
│ ══ Connected Tab ════════════════════════════════════════════════   │
│                                                                     │
│  ┌─────────────────────────────────────────────────────────────-┐   │
│  │  [GitHub logo]                                                │   │
│  │  GitHub                        ✓ Connected                    │   │
│  │  23 repos · Updated 5m ago                                    │   │
│  │  Scope: Global (all projects)                                 │   │
│  │  [Browse] [Refresh] [Disconnect]                              │   │
│  ├───────────────────────────────────────────────────────────────┤   │
│  │  [Notion logo]                                                │   │
│  │  Notion                        ✓ Connected                    │   │
│  │  2 workspaces · 14 pages indexed                              │   │
│  │  Scope: Auth Rewrite, Q4 Campaign                             │   │
│  │  [Browse] [Manage] [Disconnect]                               │   │
│  ├───────────────────────────────────────────────────────────────┤   │
│  │  [Linear logo]                                                │   │
│  │  Linear                        ✓ Connected                    │   │
│  │  1 team · 3 projects                                          │   │
│  │  Scope: Global                                                │   │
│  │  [Browse] [Disconnect]                                        │   │
│  └───────────────────────────────────────────────────────────────┘   │
│                                                                     │
│ ══ Available Tab ════════════════════════════════════════════════   │
│                                                                     │
│  ┌────────────┐  ┌────────────┐  ┌────────────┐  ┌────────────┐   │
│  │  Sentry    │  │  Jira      │  │  Figma     │  │  Slack     │   │
│  │  ○ Not     │  │  ○ Not     │  │  ○ Not     │  │  ○ Not     │   │
│  │  connected │  │  connected │  │  connected │  │  connected │   │
│  │  [Connect] │  │  [Connect] │  │  [Connect] │  │  [Connect] │   │
│  └────────────┘  └────────────┘  └────────────┘  └────────────┘   │
│                                                                     │
│ ══ Workspace Roots Tab ══════════════════════════════════════════   │
│                                                                     │
│  Local directories scanned for repositories and used as             │
│  session working directories.                                       │
│                                                                     │
│  ┌───────────────────────────────────────────────────────────────┐   │
│  │  ~/code              24 repos found    [Scan] [Remove]        │   │
│  │  ~/personal/projects  3 repos found    [Scan] [Remove]        │   │
│  │                                                                │   │
│  │  [+ Add Root Directory]                                        │   │
│  └───────────────────────────────────────────────────────────────┘   │
│                                                                     │
└─────────────────────────────────────────────────────────────────────┘
```

### Key Design Decisions — Data Sources

1. **"Data Sources" replaces "Integrations" as the user-facing concept.** The word "integration" is developer jargon. "Data Source" communicates what these services actually provide: context data for sessions.

2. **Scope indicator** — each connected source shows whether it's available globally or attached to specific projects. This surfaces the global-vs-project attachment model without requiring a separate UI.

3. **"Browse" action** navigates to the source's dedicated browsing UI (e.g., `/github/:owner/:repo`). This replaces the GitHub sidebar panel pattern — browsing is a full-page experience linked from the data source card, not a separate sidebar view.

4. **Workspace Roots** are folded into Data Sources as a tab. They're conceptually the same thing — local filesystem roots are a "data source" that provides repository and directory context. This replaces the standalone "Repositories" icon rail item.

5. **"Available" tab** shows sources that *could* be connected but aren't yet. This serves as both a feature discovery mechanism and an upsell surface for plugin expansion.

6. **Connected sources panel (sidebar)** shows a compact list of connected sources with status dots, providing quick access without navigating to the full Data Sources page.

---

## 6. Key UX Principles

### P1: Projects are the mental model
Users think in projects: "I'm working on the Auth Rewrite." Navigation should reflect this. The sidebar project tree, project detail screen, and project-scoped URLs all reinforce that sessions belong to a project.

### P2: Fleet is the telescope, not the microscope
Fleet shows everything at once — a 10,000-foot operational view. You use Fleet to spot problems (stuck sessions, runaway costs) and to navigate to the right project. You don't *work* in Fleet; you work in a project.

### P3: Scratch is a real project, not a junk drawer
Scratch always exists, appears at the top of the project list with a distinct icon (e.g., a notepad), and has its own project detail view. Sessions land here by default if no project is selected. Users can move sessions from Scratch to a named project.

### P4: Data sources attach, not configure
Users "attach" a GitHub repo or Notion page to a project, like pinning a document to a board. The mental model is attachment, not configuration. Global sources (available to all projects) vs. project-specific sources are visually distinguished.

### P5: The sidebar is for navigation, not for browsing
The sidebar shows *where you can go* (projects, sessions, data sources). Deep browsing of content (GitHub repos, Notion pages) happens in the main content area via dedicated routes. The sidebar panel is a quick-nav tree, not a content browser.

### P6: Consistent card vocabulary
Session cards look the same everywhere: in Fleet, in project detail, in the sidebar panel. The same status dot (green=working, gray=idle), the same badges (conductor, child, archived), the same actions (terminate, resume, delete). Users learn the vocabulary once.

### P7: Progressive disclosure in settings
Settings splits into three conceptual areas:
- **Agent Configuration** — Skills, Agents, Providers, Harnesses (how the AI works)
- **Data Sources** — moved to its own top-level page
- **App Preferences** — Appearance, Keybindings, About (how the app looks/feels)

This reduces the 10-tab settings page to two focused groups of 4–5 tabs each.

### P8: Desktop-first density
Session cards, project trees, and data source lists are designed for mouse-driven desktop use with compact spacing (gap-2 to gap-3, text-xs to text-sm). Touch targets expand on mobile via `pointer-coarse` media queries (already in use). No excessive whitespace.

---

## 7. Low-Fidelity Wireframes

### Wireframe A: Sidebar with Projects Panel Active

```
╔══════╦═════════════════════════════╦══════════════════════════════════════════════╗
║ RAIL ║  PROJECTS PANEL             ║  MAIN CONTENT                               ║
║      ║                             ║                                              ║
║ [⊞]  ║  ┌─ Fleet ──────────── ⊞ ┐ ║  ┌─ Header ──────────────────────────────┐   ║
║      ║  └────────────────────────┘ ║  │  Auth Rewrite    12 sessions · $4.23  │   ║
║ [📁] ║  ┌─ Projects ─── [+] [+] ┐ ║  │  [+ New Session] [⚙ Project Settings] │   ║
║  ↑   ║  │                        │ ║  └────────────────────────────────────────┘   ║
║ ACTV ║  │  📓 Scratch            │ ║                                              ║
║      ║  │    ├─ quick-fix    ○   │ ║  [Sessions]  [Data Sources]  [Activity]      ║
║ [🔌] ║  │    └─ one-off     ■   │ ║                                              ║
║      ║  │                        │ ║  ── Active Sessions ─────────────────────     ║
║      ║  │  📂 Auth Rewrite  ← ● │ ║                                              ║
║      ║  │    ├─ fix-login    ●   │ ║  ┌─────────────┐ ┌─────────────┐             ║
║      ║  │    ├─ add-tests    ○   │ ║  │ ● fix-login │ │ ● refactor  │             ║
║      ║  │    └─ refactor     ●   │ ║  │   working   │ │   working   │             ║
║      ║  │                        │ ║  │   3.2k tok  │ │   890 tok   │             ║
║      ║  │  📂 Q4 Campaign       │ ║  └─────────────┘ └─────────────┘             ║
║      ║  │    └─ copy-draft   ○   │ ║                                              ║
║      ║  │                        │ ║  ┌─────────────┐                             ║
║      ║  └────────────────────────┘ ║  │ ○ add-tests │                             ║
║──────║                             ║  │   idle      │                             ║
║      ║                             ║  │   1.1k tok  │                             ║
║ [📊] ║                             ║  └─────────────┘                             ║
║ [⚙]  ║                             ║                                              ║
║ v1   ║                             ║  ── Recent ──────────────────────────────     ║
╚══════╩═════════════════════════════╩══════════════════════════════════════════════╝

Legend:
  ● = working (green pulse)     ← = current/active project
  ○ = idle (gray)               ■ = stopped
```

### Wireframe B: Fleet Dashboard (Project Grouping)

```
╔══════╦═════════════════════════════╦══════════════════════════════════════════════╗
║ RAIL ║  FLEET PANEL                ║  MAIN CONTENT                               ║
║      ║                             ║                                              ║
║ [⊞]  ║  ┌─ Live Sessions ───────┐ ║  ┌─ Header ──────────────────────────────┐   ║
║  ↑   ║  │                        │ ║  │  ⊞ Fleet                              │   ║
║ ACTV ║  │  ● fix-login           │ ║  │  47 sessions · 3 active · $12.40     │   ║
║      ║  │    Auth Rewrite        │ ║  │  [+ New Session]                      │   ║
║ [📁] ║  │  ● refactor            │ ║  └────────────────────────────────────────┘   ║
║      ║  │    Auth Rewrite        │ ║                                              ║
║ [🔌] ║  │  ○ copy-draft          │ ║  ┌─ Summary ────────────────────────────┐    ║
║      ║  │    Q4 Campaign         │ ║  │ ⚡3 Active  ⏸2 Idle  #24k  📋0     │    ║
║      ║  │  ○ add-tests           │ ║  └───────────────────────────────────────┘    ║
║      ║  │    Auth Rewrite        │ ║                                              ║
║      ║  │  ○ quick-fix           │ ║  ┌─ Toolbar ────────────────────────────┐    ║
║      ║  │    Scratch             │ ║  │ [🔍 Search…]                         │    ║
║      ║  │                        │ ║  │ Group:[Project▾] Sort:[Recent▾]       │    ║
║      ║  │  ── Stopped ─────────  │ ║  │ Show:[Active▾]                       │    ║
║      ║  │  ■ auth-flow           │ ║  └───────────────────────────────────────┘    ║
║      ║  │  ■ db-schema           │ ║                                              ║
║      ║  │  ■ one-off             │ ║  ── Auth Rewrite (3) ── [→ Project] ──       ║
║      ║  │                        │ ║  ┌─────────────┐ ┌─────────────┐ ┌────────┐  ║
║      ║  └────────────────────────┘ ║  │ ● fix-login │ │ ○ add-tests │ │● refac │  ║
║──────║                             ║  └─────────────┘ └─────────────┘ └────────┘  ║
║ [📊] ║                             ║                                              ║
║ [⚙]  ║                             ║  ── Q4 Campaign (1) ── [→ Project] ──       ║
║ v1   ║                             ║  ┌──────────────┐                            ║
║      ║                             ║  │ ○ copy-draft │                            ║
║      ║                             ║  └──────────────┘                            ║
║      ║                             ║                                              ║
║      ║                             ║  ── Scratch (2) ─────────────────────────    ║
║      ║                             ║  ┌─────────────┐ ┌──────────────┐            ║
║      ║                             ║  │ ○ quick-fix │ │ ■ one-off    │            ║
║      ║                             ║  └─────────────┘ └──────────────┘            ║
╚══════╩═════════════════════════════╩══════════════════════════════════════════════╝
```

### Wireframe C: Data Sources Page (Connected Tab)

```
╔══════╦══════════════════════════════════════════════════════════════════════════╗
║ RAIL ║  MAIN CONTENT (no panel — link page like Settings)                     ║
║      ║                                                                        ║
║ [⊞]  ║  ┌─ Header ──────────────────────────────────────────────────────┐     ║
║      ║  │  🔌 Data Sources                                              │     ║
║ [📁] ║  │  Connect services to provide context for agent sessions       │     ║
║      ║  └────────────────────────────────────────────────────────────────┘     ║
║ [🔌] ║                                                                        ║
║  ↑   ║  [Connected]   [Available]   [Workspace Roots]                         ║
║ ACTV ║                                                                        ║
║      ║  ┌─────────────────────────────────────────────────────────────────┐    ║
║      ║  │                                                                 │    ║
║      ║  │  ┌─────────────────────────────────────────────────────────┐    │    ║
║      ║  │  │ [GH]  GitHub               ✓ Connected                 │    │    ║
║      ║  │  │       23 repos cached · updated 5m ago                  │    │    ║
║      ║  │  │       Scope: 🌐 Global (all projects)                  │    │    ║
║      ║  │  │       [Browse →]    [Refresh]    [Disconnect]           │    │    ║
║      ║  │  └─────────────────────────────────────────────────────────┘    │    ║
║      ║  │                                                                 │    ║
║      ║  │  ┌─────────────────────────────────────────────────────────┐    │    ║
║──────║  │  │ [N]   Notion                ✓ Connected                 │    │    ║
║      ║  │  │       2 workspaces · 14 pages                           │    │    ║
║ [📊] ║  │  │       Scope: 📂 Auth Rewrite, 📂 Q4 Campaign          │    │    ║
║ [⚙]  ║  │  │       [Browse →]    [Manage Pages]    [Disconnect]     │    │    ║
║ v1   ║  │  └─────────────────────────────────────────────────────────┘    │    ║
║      ║  │                                                                 │    ║
║      ║  │  ┌─────────────────────────────────────────────────────────┐    │    ║
║      ║  │  │ [Li]  Linear                ✓ Connected                 │    │    ║
║      ║  │  │       1 team · 3 projects                               │    │    ║
║      ║  │  │       Scope: 🌐 Global                                 │    │    ║
║      ║  │  │       [Browse →]    [Disconnect]                        │    │    ║
║      ║  │  └─────────────────────────────────────────────────────────┘    │    ║
║      ║  │                                                                 │    ║
║      ║  └─────────────────────────────────────────────────────────────────┘    ║
╚══════╩══════════════════════════════════════════════════════════════════════════╝
```

---

## Navigation Flow Summary

```
                    ┌──────────────┐
                    │   App Start  │
                    └──────┬───────┘
                           │
              ┌────────────┼────────────────┐
              ▼            ▼                ▼
        ┌──────────┐ ┌──────────┐    ┌──────────────┐
        │  Fleet   │ │ Projects │    │ Data Sources  │
        │ /fleet   │ │ (sidebar)│    │ /data-sources │
        └────┬─────┘ └────┬─────┘    └──────────────┘
             │            │
             │     ┌──────┴──────┐
             │     ▼             ▼
             │  ┌──────────┐ ┌──────────┐
             │  │ Scratch   │ │ Project  │
             │  │ /projects │ │ /projects│
             │  │ /scratch  │ │ /{id}    │
             │  └────┬─────┘ └────┬─────┘
             │       │            │
             └───────┴─────┬──────┘
                           ▼
                    ┌──────────────┐
                    │   Session    │
                    │  /projects/  │
                    │  {id}/       │
                    │  sessions/   │
                    │  {sessionId} │
                    └──────────────┘
```

---

## Migration Notes (Current → Proposed)

| Current | Proposed | Migration Approach |
|---|---|---|
| `/` = Fleet page | `/fleet` = Fleet page, `/` redirects to last project or Fleet | Add route alias; preserve `/` as Fleet initially, switch default later |
| `/sessions/{id}` | `/projects/{projectId}/sessions/{id}` | Support both URLs during transition; old URL redirects |
| Sidebar "Fleet" panel = project tree | Sidebar "Projects" panel = project tree | Rename; same component |
| Sidebar "Repositories" icon | Removed — workspace roots under Data Sources | Remove icon, add tab |
| Sidebar "GitHub" icon (plugin) | Removed — GitHub accessible via Data Sources "Browse" | Plugin still contributes routes, but sidebar item suppressed |
| Settings → Integrations tab | Data Sources top-level page | Move integration management out of Settings |
| Settings → Workspace Roots tab | Data Sources → Workspace Roots tab | Move to Data Sources page |
| `SummaryBar` groups by directory | Groups by Project (default) | Add "Project" grouping option, make it default |
| `FleetToolbar` GroupBy lacks "project" | Add "project" to GroupBy union type | Extend existing type |
| Sessions created without project → null projectId | Sessions without project → Scratch project ID | `EnsureScratchProjectAsync` already exists |

---

## Settings Reorganization

### Proposed Tab Structure

**Settings page (`/settings`)**
```
[Agent Config]  [Preferences]  [About]
```

**Agent Config sub-tabs:**
- Skills (existing)
- Agents (existing)
- Providers (existing)
- Harnesses (existing)

**Preferences sub-tabs:**
- Appearance (existing)
- Keybindings (existing)
- API Keys (cloud mode only, existing)

**Removed from Settings:**
- Integrations → moved to `/data-sources`
- Workspace Roots → moved to `/data-sources` → Workspace Roots tab

This drops Settings from 10 tabs to 6-7 (or 2 groups of 3-4), making it easier to scan.

---

## Scratch Project Behavior

| Behavior | Detail |
|---|---|
| Always exists | Created on first launch by `EnsureScratchProjectAsync` (already implemented) |
| Cannot be deleted | UI hides delete option; backend rejects deletion |
| Cannot be renamed | Name is always "Scratch" |
| Pinned position | Always first in project list sidebar |
| Visual distinction | Notepad/pencil icon instead of folder icon |
| Default project | New sessions without explicit project selection go here |
| Session movement | Users can move sessions from Scratch to any named project (drag or context menu) |
| Project detail view | Scratch has its own project detail page at `/projects/scratch` |
| Fleet grouping | In Fleet dashboard, Scratch group appears last |

---

## What This Concept Does NOT Change

- Session detail screen layout (activity stream, message rendering, tool calls, delegation cards)
- Session card component design (same LiveSessionCard)
- Real-time event model (WebSocket, SSE)
- Backend API shape (projects, sessions, plugins — existing endpoints preserved)
- Command palette behavior
- Analytics page structure
- Plugin contribution model (FleetPluginManifest, contributions)

The concept is additive — it reorganizes navigation and adds a project detail screen without rewriting existing components.
