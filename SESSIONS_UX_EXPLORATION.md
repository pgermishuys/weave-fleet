# Weave Agent Fleet — Sessions UX Design Exploration

**Project:** `D:\repos\damianh\weave-agent-fleet`  
**Latest Commit:** `5ff2c3a` (Merge PR #195 – sidebar toggle fix)  
**Date:** May 10, 2026

---

## 1. Session List & Panel Layout

### Main Fleet Page (`src/app/page.tsx`)

The **fleet dashboard** is the primary view for managing sessions. It follows a **multi-pane layout**:

```
┌─────────────────────────────────────────────────────┐
│ Header: "Agent Fleet" + "X active sessions" button  │
│ [New Session] button                                │
├─────────────────────────────────────────────────────┤
│ Summary Bar (grid of 4 metrics)                      │
│ • Active (green Zap icon)                           │
│ • Idle (muted Pause icon)                           │
│ • Tokens (purple Hash icon)                         │
│ • Queued (orange ListTodo icon)                     │
├─────────────────────────────────────────────────────┤
│ Fleet Toolbar (sticky)                              │
│ [Search: "Search sessions…"] [Group ▼] [Sort ▼]   │
├─────────────────────────────────────────────────────┤
│ Session Grid / List Area                            │
│ (responsive: 1 col mobile, 2 col tablet, 3-4 cols) │
└─────────────────────────────────────────────────────┘
```

**Key dimensions:**
- Page padding: `p-3 sm:p-4 lg:p-6` (responsive)
- Grid gap: `gap-3 sm:gap-4`
- Grid columns: `sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4`
- Full width flex layout with scrollable overflow

---

### Sidebar Panel (`src/components/layout/sidebar.tsx`, `fleet-panel.tsx`)

The **left sidebar** provides a **hierarchical tree view** of sessions grouped by workspace:

```
┌─────────────────────────────┐
│ ▸ Fleet Icon (active)       │
│ ▸ GitHub Icon               │
│ ▸ Repositories Icon         │
│ ▸ Pipelines Icon            │
│ ▸ Queue Icon                │
│ ▸ Settings Icon             │
└─────────────────────────────┘
     ↓ Opens panel when clicked
     
┌──────────────────────────────────┐
│ Contextual Panel (Fleet Sidebar) │
│                                  │
│ 👁️ Hide Inactive  🗑️ Remove Inactive
│ (toggle + delete button)         │
│                                  │
│ ► Workspace 1  [Count]           │
│   ├─ Session A [● working] 📌    │
│   ├─ Session B [idle]            │
│   └─ Session C [● stopped]       │
│                                  │
│ ► Workspace 2  [Count]           │
│   └─ Session D [● working]       │
│                                  │
│ ► Workspace 3  [Count]           │
│   (no running sessions visible)  │
└──────────────────────────────────┘
```

**Layout details:**
- **Desktop:** Fixed aside bar (resizable, 60px icon rail + panel)
- **Mobile:** Sheet drawer (swipe from left edge, 280px width)
- **Collapsible workspaces:** Per-workspace collapse state persisted (`weave:sidebar:collapsed`)
- **Pinning:** Workspaces can be pinned to top (`weave:sidebar:pinned`)
- **Hide inactive toggle:** Filters out stopped/completed/disconnected sessions (`weave:sidebar:hideInactive`)

---

### Session Card Component (`src/components/fleet/live-session-card.tsx`)

Each session is rendered as a **card** in the grid or sidebar:

```
┌──────────────────────────────────────────┐
│ ● Session Title             [→ Hover]    │ ← Becomes link
│ working  ⚙️ worktree conductor  |abc…   │
├──────────────────────────────────────────┤
│ 🕐 2 hours ago       5.2K tokens | $0.23 │
│                        #abc123…         │
└──────────────────────────────────────────┘
```

**Card header (pt-4 px-4 pb-2):**
- Activity status dot: `h-2.5 w-2.5 rounded-full`
  - **Busy:** `bg-green-500 animate-pulse` (pulsing green)
  - **Idle:** `bg-muted-foreground/50` (dim gray)
- Session title (truncated, max 140px)
- Arrow icon on hover (opacity: 0 → 100)

**Card metadata row:**
- Status badge: `working` or `idle` (secondary variant, text-[10px])
- Connection icons (only shown when unhealthy):
  - `WifiOff` (red) = disconnected
  - `Square` (gray) = stopped/completed
- Isolation strategy badge:
  - `worktree` = `GitBranch` icon (purple)
  - `clone` = `Copy` icon (purple)
  - Tooltip on hover
- Parent/child badges (cyan for conductor, orange for child)
- Directory path (only for existing isolation, monospace, 120px max)

**Card footer (px-4 pb-4):**
- Clock icon + relative time (`2 hours ago`)
- Token count + cost breakdown (if available)
- Session ID prefix (`#abc123…`)

**Action buttons (top-right corners):**
- **Visible on hover/focus** (opacity: 0 → 100, pointer-coarse always visible on mobile):
  - **Terminate** (trash): `hover:text-destructive` (red)
  - **Abort** (octagon-x): `hover:text-amber-500` (yellow, only if `activityStatus === "busy"`)
  - **Resume** (rotate-ccw): `hover:text-green-500` (only if inactive)
  - **Delete** (trash, red variant): Only for stopped/completed/disconnected

**Link behavior:**
- Card is a `<Link>` to `/sessions/[sessionId]?instanceId=[instanceId]`
- Link wrapping preserves onClick handlers for action buttons
- Inactive sessions have `opacity-60`

---

## 2. Session Grouping

### Grouping Strategies (`GroupBy` type)

The fleet toolbar allows switching between **5 grouping modes**, persisted in localStorage:

```typescript
type GroupBy = "directory" | "session-status" | "connection-status" | "source" | "none";
```

**Default:** `"directory"` (group by workspace)

#### 2.1 Directory Grouping (Default)
**File:** `src/components/fleet/session-group.tsx`

Sessions are grouped into `WorkspaceGroup` objects using `groupSessionsByWorkspace()`:

```
┌─────────────────────────────────────┐
│ ▼ my-project                    [4]  │  ← Collapsible header
│   ├─ Session A [● working]          │
│   ├─ Session B [idle]               │
│   └─ Session C [● child]            │  ← Nested child (parent-child hierarchy)
│                                     │
│ ▼ another-repo                  [2]  │
│   ├─ Session D [● working]          │
│   └─ Session E [● stopped]          │
└─────────────────────────────────────┘
```

**SessionGroup component features:**
- **Collapsible header:**
  - Chevron icon (rotates on expand): `rotate-90` when open
  - Status dot: green (pulsing) if any session running, dim gray otherwise
  - Workspace display name (inline-editable via `InlineEdit`)
  - Session count badge (secondary variant)
  - Overflow menu (⋮)

- **Overflow menu (DropdownMenu):**
  - "New Session" (if `onNewSession` prop provided)
  - "Open in [IDE/Terminal/Editor]" (submenu)
  - "Terminate All" (destructive, red variant)

- **Collapse state:** Persisted per workspace (`weave:fleet:collapsed`)

- **Sessions within group:**
  - Nested via `nestSessions()` → parent cards + left-indented child cards
  - Children have left border: `border-l-2 border-muted-foreground/20 pl-3 ml-4`
  - Each child marked with orange `child` badge

#### 2.2 Session Status Grouping
**Visual grouping by activity:**
```
WORKING (3)
┌─ Card: Session A [● working]
├─ Card: Session B [● working]
└─ Card: Session C [● working]

IDLE (2)
┌─ Card: Session D [idle]
└─ Card: Session E [idle]
```

Rendered as sections with:
- Uppercase label: `text-xs font-semibold uppercase tracking-wider text-muted-foreground`
- Count badge in parens
- Grid layout (`sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4`)

#### 2.3 Connection Status Grouping
**Visual grouping by lifecycle:**
```
CONNECTED (5)
[cards]

DISCONNECTED (1)
[cards]

STOPPED (2)
[cards]
```

Lifecycle order: `running → completed → stopped → error → disconnected`

#### 2.4 Source Grouping
**Visual grouping by isolation strategy:**
```
EXISTING (7)
[cards]

WORKTREE (3)
[cards]

CLONE (1)
[cards]
```

#### 2.5 No Grouping
Flat grid of all sessions (sorted by current sort preference).

---

### Project/Workspace Concept

**WorkspaceGroup interface** (`src/lib/workspace-utils.ts`):
```typescript
interface WorkspaceGroup {
  workspaceId: string;
  workspaceDirectory: string;        // e.g., "/Users/me/my-project"
  displayName: string;               // User-customizable, falls back to dir basename
  sessionCount: number;              // Total sessions (including inactive)
  hasRunningSession: boolean;        // At least one running + healthy instance
  sessions: SessionListItem[];       // All sessions in this workspace
}
```

**Multiple sessions per workspace:**
- Each session has `workspaceId` + `workspaceDirectory`
- Workspaces with same directory are merged into one group
- Users can **rename** workspace display name inline (persisted in Fleet DB)

**Workspace filtering:**
- Sidebar link: `/?workspace=[workspaceId]` filters fleet page to that workspace
- `filterSessionsByWorkspace()` resolves workspaceId → directory, returns all sessions in that directory

---

### Parent-Child Hierarchy

Sessions can have **parent-child relationships** (conductor pattern):

```typescript
interface SessionListItem {
  dbId?: string;              // Fleet DB session ID (for parent lookup)
  parentSessionId?: string;   // Fleet DB session ID of parent
}
```

**Nesting logic** (`src/lib/session-utils.ts`):
- `nestSessions(items)` groups sessions by `parentSessionId` → `dbId` matching
- Returns `NestedSession[]`:
  ```typescript
  interface NestedSession {
    item: SessionListItem;     // Parent session
    children: SessionListItem[]; // All sessions with parentSessionId === item.dbId
  }
  ```

**Visual indicators:**
- Parent cards: cyan `conductor` badge
- Child cards: orange `child` badge, left-indented (border-left accent)

---

## 3. Session Actions

### Available Actions

**On fleet page (cards):**

| Action | Icon | Condition | Placement | Visual |
|--------|------|-----------|-----------|--------|
| **Navigate** | Arrow → | Always | Hover overlay | Opacity fade-in |
| **Terminate** | Trash | `lifecycleStatus !== stopped/completed` | Top-right | Opacity fade-in, hover: red |
| **Abort** | Octagon-X | `activityStatus === "busy"` AND `lifecycleStatus === "running"` | Top-right (2nd) | Opacity fade-in, hover: amber |
| **Resume** | RotateCcw | `lifecycleStatus === stopped/completed/disconnected` | Top-right (2nd) | Opacity fade-in, hover: green |
| **Delete** | Trash | `lifecycleStatus === stopped/completed/disconnected` | Top-right | Opacity fade-in, hover: red |
| **Context menu** | Right-click | Always | Right-click | |

**Context menu** (ContextMenu):
- "Open in [IDE/Terminal/etc]" (submenu)
- "Copy path" (workspace directory)

---

### In Sidebar (Session Item)

**Right-click context menu** (`src/components/layout/sidebar-session-item.tsx`):
- Rename (inline edit)
- Abort (if busy)
- Stop (if running)
- Fork
- Delete
- Resume (if stopped/completed/disconnected)
- Open in [IDE/etc]
- Copy path

**Inline actions** (visible on hover):
- Rename button (pencil)
- Stop/terminate button
- Context menu trigger

---

### Workspace Actions

**SessionGroup header overflow menu:**
- New Session
- Open in [IDE/etc]
- Terminate All

**Sidebar workspace right-click menu:**
- Rename
- Pin/Unpin
- New Session
- Open in [IDE/etc]
- Terminate All
- Remove Inactive

**Hide Inactive toggle:**
- Global toggle in sidebar header
- Filters sidebar + shows option to delete all inactive sessions
- "Remove All Inactive" button with confirmation dialog

---

## 4. Session States & Visual Indicators

### Activity Status (What the agent is doing)

```typescript
type SessionActivityStatus = "busy" | "idle" | "waiting_input";
```

**Visual indicators:**
- **Busy:** Green pulsing dot `● animate-pulse`
- **Idle:** Dim gray dot
- **Waiting Input:** (implied same as idle in current UI)

### Lifecycle Status (Overall session state)

```typescript
type SessionLifecycleStatus = 
  | "running"        // Process is healthy
  | "completed"      // Finished normally
  | "stopped"        // Terminated/paused
  | "error"          // Crashed
  | "disconnected";  // Lost connection to process
```

**Connection icons** (only shown when unhealthy):
- **Disconnected:** `WifiOff` icon (red)
- **Stopped/Completed:** `Square` icon (gray)
- Tooltip on hover

**Card opacity:**
- Inactive (stopped/completed/disconnected): `opacity-60`
- Running: full opacity

### Token & Cost Data

**Tracked in SessionListItem:**
```typescript
totalTokens?: number;      // Aggregate across all messages
totalCost?: number;        // USD cost
```

**Display:**
- Compact breakdown in card footer: `[count] tokens | $[cost]`
- Component: `<TokenCostBreakdown variant="compact" />`

---

## 5. Session Navigation

### Fleet Page → Session Detail

**Clicking a session card:**
1. Card is a `<Link>` element
2. Navigates to `/sessions/[sessionId]?instanceId=[instanceId]`
3. Routes to `src/app/sessions/[id]/page.tsx`

**Session detail page features:**
- Full activity stream (messages, tool calls, agent switches, etc.)
- Prompt input area (bottom)
- Tabs for:
  - Activity (default)
  - Files (code diff viewer)
  - Todos (extracted from session)
  - GitHub Links (PRs/issues referenced)
- Session metadata header:
  - Session title (editable)
  - Status badges
  - Action dropdown (fork, spawn, terminate, delete, etc.)
  - Token cost breakdown

**Context preservation:**
- `useSessionsContext()` syncs title patches to sidebar immediately
- SSE subscription for real-time activity updates
- Message pagination for performance

### Sidebar → Fleet or Session Detail

**Workspace item click:**
- Navigates to `/?workspace=[workspaceId]` (filters fleet page)

**Session item click:**
- Navigates to `/sessions/[sessionId]?instanceId=[instanceId]`
- Same as card click

**Active session styling:**
- Sidebar session item: `isActive` prop highlights current page
- Fleet page workspace: `isActiveWorkspace` highlights if workspace filter matches

---

## 6. Fleet Dashboard & Overview

### Summary Bar (`src/components/fleet/summary-bar.tsx`)

**4-column grid** (2 col on mobile, 4 on desktop):

```
┌──────────┬──────────┬──────────┬──────────┐
│ ⚡ 5     │ ⏸ 3     │ # 125K   │ 📋 0     │
│ Active   │ Idle     │ Tokens   │ Queued   │
└──────────┴──────────┴──────────┴──────────┘
```

Each metric card:
- **Icon** (Zap=green, Pause=muted, Hash=purple, ListTodo=orange)
- **Value** (bold, large)
- **Label** (xs, muted-foreground)
- Border + bg-card styling

**Data source:**
- `useFleetSummary()` hook polls `/api/fleet/summary` (30s interval)
- Or derived from session list when summary unavailable:
  ```typescript
  activeSessions: count where activityStatus === "busy"
  idleSessions: count where lifecycleStatus === "running" && activityStatus === "idle"
  totalTokens: sum of all session tokens
  totalCost: sum of all session costs
  queuedTasks: 0 (reserved for future queue feature)
  ```

---

## 7. Session Filtering & Search

### Search (`src/components/fleet/fleet-toolbar.tsx`)

**Real-time search** in toolbar:
- Input placeholder: "Search sessions…"
- Searches **3 fields** (case-insensitive):
  1. Session title
  2. Workspace directory path
  3. Workspace display name

**Implementation** (`src/app/page.tsx`):
```typescript
const searchFiltered = useMemo(() => {
  const q = search.trim().toLowerCase();
  if (!q) return workspaceFiltered;
  return workspaceFiltered.filter((s) => {
    const title = s.session.title?.toLowerCase() ?? "";
    const dir = (s.sourceDirectory ?? s.workspaceDirectory).toLowerCase();
    const displayName = s.workspaceDisplayName?.toLowerCase() ?? "";
    return title.includes(q) || dir.includes(q) || displayName.includes(q);
  });
}, [workspaceFiltered, search]);
```

**No results states:**
- Search text entered but no matches: "No sessions match your search."
- Workspace filter but no sessions: "No sessions in this workspace."
- Empty fleet: "No sessions running." + hint to create new session

---

### Workspace Filter (URL parameter)

**Fleet toolbar not visible**, but **sidebar provides filter:**
- Click workspace in sidebar → `/?workspace=[workspaceId]`
- Filters all views to that workspace
- `filterSessionsByWorkspace()` resolves ID to directory, returns all sessions

---

### Sort By

**3 sort options** (persisted in localStorage: `weave:fleet:prefs`):

| Sort | Order | Logic |
|------|-------|-------|
| **Recent** (default) | Descending | `session.time.created` (newest first) |
| **Name** | A-Z | Session title (or ID if untitled), case-insensitive |
| **Status** | Activity first, then lifecycle | `busy → waiting_input → idle` then `running → completed → stopped → error → disconnected` |

**Applied per group** (workspace or session-status group).

---

### Group By

**5 grouping modes** (persisted in localStorage: `weave:fleet:prefs`):

| Group | Logic |
|-------|-------|
| **Directory** | `WorkspaceGroup` by workspace (default) |
| **Session Status** | `working` (busy) vs `idle` |
| **Connection Status** | `connected` vs `disconnected` vs `stopped` |
| **Source** | `isolationStrategy` (`existing` / `worktree` / `clone`) |
| **None** | Flat grid, only sorted |

---

## 8. Real-Time Updates & State Management

### SessionsContext (`src/contexts/sessions-context.tsx`)

**Hybrid polling + SSE strategy:**

1. **Polling** (15s interval):
   - `useSessions()` hook polls `/api/sessions`
   - Updates `polledSessions` array
   - Structural sharing: only updates state if data changed

2. **Server-Sent Events (SSE):**
   - `useGlobalSSE()` subscribes to shared singleton
   - Handles real-time activity status updates: `activity_status` event
   - Handles token updates: `token_update` event
   - Patches applied via `patchActivityStatus()`, `patchTokenData()` to avoid stale data

3. **Smart patch pruning:**
   - SSE patches kept only while ahead of poll
   - When poll catches up, patches pruned
   - Prevents inconsistent state

4. **Optimistic updates:**
   - `patchSessionTitle()` → immediate UI update on rename
   - `patchWorkspaceDisplayName()` → immediate workspace rename
   - Patches cleared when poll confirms

### Context Value

```typescript
interface SessionsContextValue {
  sessions: SessionListItem[];          // Merged polled + SSE patches
  isLoading: boolean;                   // Initial load state
  error?: string;                       // Poll/fetch errors
  refetch: () => void;                  // Manual refetch trigger
  summary: FleetSummaryResponse | null; // Fleet metrics
  patchSessionTitle: (sessionId, title) => void;
  patchWorkspaceDisplayName: (workspaceId, displayName) => void;
}
```

---

## 9. Responsive Design & Mobile

### Breakpoints

| Screen | Behavior |
|--------|----------|
| **Mobile** (`< sm`)  | Single-column grid, sheet drawer sidebar, abbreviated labels |
| **Tablet** (`sm+`)  | 2-column grid, inline sidebar (resizable) |
| **Desktop** (`lg+`) | 3-column grid, full sidebar visible |
| **Large** (`xl+`)   | 4-column grid |

### Mobile Sidebar

- **Sheet drawer** (overlay, not inline)
- **Swipe-to-open:** Touches within 24px of left edge trigger drawer open
- Dismissible by clicking outside or close button
- Full height, 280px width

### Touch Optimization

- Action buttons on cards: `pointer-coarse:opacity-100 pointer-coarse:h-9 pointer-coarse:w-9`
  - Always visible on touch devices (no hover state)
  - Slightly larger (36px vs 32px)

---

## 10. Key File Locations & Component Tree

```
src/
├─ app/
│  ├─ page.tsx                      ← Fleet dashboard main entry
│  ├─ sessions/[id]/page.tsx        ← Session detail page
│  └─ client-layout.tsx             ← Client-side providers + sidebar
│
├─ components/
│  ├─ layout/
│  │  ├─ sidebar.tsx                ← Sidebar main (icon rail + contextual panel)
│  │  ├─ fleet-panel.tsx            ← Fleet sidebar content
│  │  ├─ sidebar-workspace-item.tsx ← Collapsible workspace group
│  │  └─ sidebar-session-item.tsx   ← Individual session in sidebar
│  │
│  ├─ fleet/
│  │  ├─ fleet-toolbar.tsx          ← Search + Group + Sort controls
│  │  ├─ live-session-card.tsx      ← Grid/flat card rendering
│  │  ├─ session-group.tsx          ← Collapsible workspace group (fleet page)
│  │  ├─ summary-bar.tsx            ← 4-metric dashboard
│  │  └─ confirm-delete-session-dialog.tsx ← Delete confirmation modal
│  │
│  └─ layout/header.tsx             ← Page header + New Session button
│
├─ contexts/
│  ├─ sessions-context.tsx          ← Polling + SSE + optimistic patches
│  ├─ sidebar-context.tsx           ← Panel width, collapse state, mobile drawer
│  └─ keybindings-context.tsx       ← Keyboard shortcuts
│
├─ hooks/
│  ├─ use-workspaces.ts             ← Group sessions by workspace
│  ├─ use-sessions.ts               ← Poll /api/sessions
│  ├─ use-terminate-session.ts      ← POST /api/sessions/[id]?action=terminate
│  ├─ use-delete-session.ts         ← DELETE /api/sessions/[id]
│  ├─ use-resume-session.ts         ← POST /api/sessions/[id]/resume
│  ├─ use-abort-session.ts          ← POST /api/sessions/[id]/abort
│  ├─ use-rename-session.ts         ← PATCH /api/sessions/[id] title
│  ├─ use-create-session.ts         ← POST /api/sessions
│  ├─ use-fork-session.ts           ← POST /api/sessions/[id]/fork
│  └─ use-fleet-summary.ts          ← Poll /api/fleet/summary
│
└─ lib/
   ├─ types.ts                      ← FleetSummary, SessionActivityStatus, etc.
   ├─ api-types.ts                  ← SessionListItem, API request/response shapes
   ├─ session-utils.ts              ← nestSessions(), isInactiveSession()
   └─ workspace-utils.ts            ← groupSessionsByWorkspace(), WorkspaceGroup type
```

---

## 11. UX Patterns & Best Practices

### 1. **Responsive Grid Layout**
- CSS grid auto-columns adapting to screen size
- Consistent gap spacing (scaled per breakpoint)
- Cards maintain fixed aspect ratio for visual balance

### 2. **Collapsible Hierarchy**
- Workspaces collapse/expand via `useSyncExternalStore` (persisted state)
- Smooth animations: `data-[state=open]:slide-in-from-top-1`
- Keyboard navigation: Arrow keys to traverse tree items

### 3. **Deferred Rendering**
- `useDeferredValue()` defers workspace group rendering
- Prevents SSE updates from blocking UI thread
- Slight visual lag (acceptable) in exchange for smooth interactions

### 4. **Inline Editing**
- Session/workspace names edited via `<InlineEdit>` component
- Click to edit, Enter to confirm, Esc to cancel
- Optimistic patch applied immediately
- Server confirmation on next poll

### 5. **Context Menus**
- Right-click opens action menu
- Consistent actions across fleet cards, sidebar items, workspaces
- Grouped by action type (info, modify, delete)

### 6. **Status Badges**
- Small, monospaced badges for activity state
- Color coding: green (working), gray (idle), red (error)
- Icon indicators for connection issues (wifi-off, stopped square)

### 7. **Loading States**
- Spinner icons (`<Loader2 className="animate-spin">`)
- Buttons disabled during async operations
- Toast/alert notifications for errors

### 8. **Empty States**
- Context-specific messages ("No sessions running", "No sessions in this workspace")
- Helpful hints for next action ("Click New Session to spawn…")

---

## 12. Summary: User Workflows

### Workflow 1: Monitor Fleet at a Glance
1. **Fleet Page** shows grid of all sessions
2. **Summary Bar** displays active, idle, token, queued counts
3. **Color-coded dots** and badges indicate session health
4. **Sidebar** shows workspace grouping with collapsible trees

### Workflow 2: Filter & Focus on Workspace
1. Click workspace in sidebar → `/? workspace=[id]`
2. Fleet page filters to that workspace
3. All grouping/sorting still applies within filtered set

### Workflow 3: Search for Session
1. Type in search box (fleet toolbar)
2. Filters by session title, workspace path, or display name
3. Grid/grouped view updates in real-time

### Workflow 4: Switch Session Status Group
1. Click "Group" dropdown → select "Session Status"
2. Fleet page reorganizes into "Working" and "Idle" sections
3. Can sort within each section (Recent, Name, or Status)

### Workflow 5: Navigate to Session Detail
1. Click session card or sidebar item
2. Navigate to `/sessions/[id]?instanceId=[id]`
3. View full activity stream, messages, file changes, tokens

### Workflow 6: Rename Session/Workspace
1. Sidebar: Click session/workspace name to edit inline
2. Or right-click → "Rename"
3. Enter new name, press Enter
4. Optimistic update immediately; server confirms on next poll

### Workflow 7: Delete Completed/Stopped Session
1. Click session card → "Delete" button (red trash)
2. Or sidebar right-click → "Delete"
3. Confirmation dialog with session title
4. Permanently removes session and related data

### Workflow 8: Terminate Running Session
1. Hover over session card → "Terminate" button (trash)
2. Or sidebar right-click → "Stop"
3. Immediate API call; session transitions to stopped
4. Can resume later if needed

### Workflow 9: Fork Session (Repeat Work)
1. Right-click session → "Fork"
2. Dialog prompts for optional new title
3. Creates child session linked to original
4. Navigates to new forked session detail page

### Workflow 10: Hide Inactive Sessions
1. Sidebar: Click "Hide Inactive" toggle
2. Grayed-out / stopped sessions hidden
3. Option to "Remove All Inactive" (confirm + delete)
4. State persisted across page reloads

---

## 13. Performance Considerations

### Polling Strategy
- **15-second interval** for session list (balance between freshness and load)
- **30-second interval** for fleet summary metrics
- Structural sharing prevents unnecessary re-renders if data unchanged

### SSE Optimization
- Real-time activity_status + token_update events
- Applied as patches (fast, targeted updates) instead of full list refresh
- `requestAnimationFrame` batching to avoid excessive re-renders

### Memoization
- Components wrapped in `React.memo` for leaf items
- `useMemo` for expensive computations (workspace grouping, filtering, sorting)
- `useCallback` for event handlers passed to memoized children

### Deferred Rendering
- `useDeferredValue()` on workspace group arrays
- Prevents synchronous blocking from rapid SSE updates

---

## 14. Types & Data Structures

### SessionListItem (`src/lib/api-types.ts`)
```typescript
interface SessionListItem {
  instanceId: string;                    // OpenCode instance ID
  workspaceId: string;                   // Fleet DB workspace ID
  workspaceDirectory: string;            // e.g., "/Users/me/my-project"
  workspaceDisplayName: string | null;   // User-customizable name
  isolationStrategy: string;             // "existing" | "worktree" | "clone"
  sessionStatus: "active" | "idle" | "stopped" | ...;
  session: Session;                      // SDK session object
  instanceStatus: "running" | "dead";
  dbId?: string;                         // Fleet DB session ID (for parent-child)
  parentSessionId?: string | null;       // Fleet DB ID of parent session
  sourceDirectory: string | null;        // Original project dir (worktree/clone)
  branch: string | null;                 // Git branch for worktree/clone
  activityStatus: SessionActivityStatus | null;  // "busy" | "idle" | "waiting_input"
  lifecycleStatus: SessionLifecycleStatus;  // "running" | "completed" | ...
  typedInstanceStatus: InstanceStatus;   // "running" | "stopped"
  totalTokens?: number;                  // Aggregate token count
  totalCost?: number;                    // USD cost
}
```

### WorkspaceGroup (`src/lib/workspace-utils.ts`)
```typescript
interface WorkspaceGroup {
  workspaceId: string;
  workspaceDirectory: string;
  displayName: string;
  sessionCount: number;
  hasRunningSession: boolean;
  sessions: SessionListItem[];
}
```

### FleetSummary (`src/lib/types.ts`)
```typescript
interface FleetSummary {
  activeSessions: number;
  idleSessions: number;
  totalTokens: number;
  totalCost: number;
  queuedTasks: number;
}
```

---

## 15. Notable Implementation Details

### 1. Session Nesting
- `nestSessions(items)` identifies parent-child pairs by matching `dbId` ↔ `parentSessionId`
- Children rendered as indented list below parent with left-border accent
- Used in both fleet page and sidebar

### 2. Workspace Merging
- Multiple sessions with same `sourceDirectory` (for worktree/clone) merge into one group
- Display name prioritized: explicit user name > directory basename fallback
- Workspace filtering resolves `workspaceId` to directory for accurate filtering

### 3. Isolation Strategy Icons
- `worktree` → GitBranch icon (purple)
- `clone` → Copy icon (purple)
- `existing` → no icon (shown as directory path text instead)

### 4. Session Status Derivation
- `activityStatus` ("busy", "idle", "waiting_input") = what agent is doing NOW
- `lifecycleStatus` ("running", "completed", "stopped", "error", "disconnected") = overall health
- Server derives activity from OpenCode SDK events; lifecycle from process health

### 5. Inactive Session Filtering
- `isInactiveSession()` returns true if `lifecycleStatus` is one of: "completed", "stopped", "error", "disconnected"
- Sidebar: "Hide Inactive" filters these out; fleet page: visibility controlled by user grouping choice

### 6. Optimistic Patches
- Title renames applied immediately via `patchSessionTitle()` (stored in ref, not state)
- On next poll, patches pruned if server confirms or session no longer exists
- Prevents flickering on rename

---

## 16. Accessibility & Keyboard Navigation

### Keyboard Shortcuts (via command palette)
- Sidebar tree navigation: Arrow Up/Down, Arrow Left/Right to expand/collapse
- F2 on workspace: triggers rename mode
- Workspaces & sessions focusable with tabindex

### ARIA Labels
- Tree items: `role="treeitem"`, `aria-label="Workspace Name"`
- Tree groups: `role="group"`
- Buttons: descriptive aria-labels (e.g., "Expand my-project")

### Focus Management
- Focus visible ring: `focus-visible:ring-1 focus-visible:ring-ring`
- Collapsible triggers stop propagation to prevent double-handling
- Mobile touch interaction doesn't require hover state

---

## Conclusion

The **Weave Agent Fleet** provides a sophisticated **multi-level session management UX** with:

- **Grid + tree layout** adapting to screen size and grouping preference
- **Real-time updates** via polling + SSE with optimistic UI patches
- **Hierarchical organization** by workspace, with parent-child session support
- **Flexible filtering, sorting, and grouping** to organize dozens of sessions
- **Responsive design** from mobile to ultra-wide displays
- **Accessibility** with keyboard navigation and ARIA semantics

The design balances **information density** (fleet overview) with **focused task workflows** (individual session management), leveraging modern React patterns (context, hooks, memoization, deferred rendering) for performance at scale.
