# Quick Visual Reference — Weave Fleet Sessions UX

## Key Visuals

### 1. Fleet Dashboard Layout
```
┌────────────────────────────────────────────────────────────────┐
│ 🎯 Agent Fleet          "3 active sessions"    [+ New Session] │
├────────────────────────────────────────────────────────────────┤
│                                                                │
│  ┌────────┬────────┬────────┬────────┐  Summary Bar            │
│  │ ⚡ 5   │ ⏸ 3   │ # 125K │ 📋 0  │                          │
│  │Active  │ Idle   │Tokens  │Queued │                          │
│  └────────┴────────┴────────┴────────┘                          │
│                                                                │
│  [Search: "Search sessions…"] [Group ▼] [Sort ▼]  Toolbar    │
│                                                                │
│  ┌─────────────────┐  ┌─────────────────┐  ┌─────────────────┐│
│  │ ● Session A     │  │ ● Session C     │  │ Session E [idle]││ Grid of cards
│  │ working  [→]    │  │ working  [→]    │  │ [⏸ stopped]  [R]││ (responsive)
│  │ 🕐 2h ago       │  │ 🕐 30m ago      │  │ 🕐 1d ago       ││
│  │#abc123…         │  │#def456…         │  │#ghi789…         ││
│  └─────────────────┘  └─────────────────┘  └─────────────────┘│
│                                                                │
│  ┌─────────────────┐  ┌─────────────────┐                      │
│  │ Session B [idle]│  │ ● Session D     │                      │
│  │ [⏸ disconnected]│  │ working  [→]    │                      │
│  │ 🕐 5h ago       │  │ 🕐 1h ago       │                      │
│  │#jkl012…         │  │#mno345…         │                      │
│  └─────────────────┘  └─────────────────┘                      │
│                                                                │
└────────────────────────────────────────────────────────────────┘
```

### 2. Sidebar Tree View (Desktop)
```
┌──────────────────────────────────┐
│ [≡][F][G][R][P][Q][⚙️]            │  ← Icon rail (60px)
├──────────────────────────────────┤
│ Workspace Fleet Panel             │  ← Main content area
│                                  │
│ 👁️ Hide Inactive  🗑️ Rm Inactive  │
│                                  │
│ ▼ my-project              [4]   │  ← Collapsible
│  └─ ● Session A           ↗      │
│  ├─ Session B (idle)      ↗      │
│  ├─ ● Session C (child)   ↗      │  ← Orange "child" badge
│  └─ ● Session D [stopped] ↗      │
│                                  │
│ ▼ another-repo            [2]   │
│  ├─ ● Session E           ↗      │
│  └─ Session F (idle)      ↗      │
│                                  │
│ ► workspace-3             [1]   │  ← Collapsed
│                                  │
└──────────────────────────────────┘
```

### 3. Session Card (Detailed)
```
┌───────────────────────────────────────────┐
│ ● Session Title              [✕ Hover]   │  ← Green dot = busy
│ working  ⚙️ worktree conductor  |abc…   │  ← Badges + path preview
├───────────────────────────────────────────┤
│ 🕐 2 hours ago   5.2K tokens | $0.23    │  ← Metadata footer
│                              #abc123…   │
└───────────────────────────────────────────┘
     ↑                    ↑           ↑
   Busy dot          Token cost   Session ID
   (pulsing)         breakdown    (preview)
```

### 4. Session Card Actions (On Hover)
```
┌───────────────────────────────────────┐
│ ● Title              [⏹] [✕] [→]     │  ← Action buttons fade in
│ working                                │
├───────────────────────────────────────┤
│ 🕐 2h ago    tokens    #abc…          │
└───────────────────────────────────────┘

Actions (left-to-right, top-right corner):
[✕] Resume (if stopped)     — green hover
[⏹] Abort (if busy)         — amber hover
[🗑️] Delete (if stopped)    — red hover
    OR
[🗑️] Terminate (if running) — red hover
```

### 5. Grouping Modes Comparison

#### Directory Grouping (Default)
```
▼ my-project        [3]
  ├─ ● Session A
  └─ Session B
▼ another-repo      [2]
  ├─ ● Session C
  └─ Session D
```

#### Session Status Grouping
```
WORKING (2)
  ├─ ● Session A
  └─ ● Session C

IDLE (2)
  ├─ Session B
  └─ Session D
```

#### Connection Status Grouping
```
CONNECTED (2)
  ├─ ● Session A
  └─ ● Session C

STOPPED (2)
  ├─ Session B
  └─ Session D
```

#### Source Grouping
```
EXISTING (2)
  ├─ ● Session A
  └─ Session B

WORKTREE (2)
  ├─ ● Session C ⚙️
  └─ Session D ⚙️
```

### 6. Status Indicators Legend

**Activity Status (agent is doing):**
```
● Busy         = Green pulsing dot
○ Idle         = Gray dim dot
⊙ Waiting      = (same as idle, visual)
```

**Lifecycle Status (overall health):**
```
🟢 Running     = Session is active
✓ Completed    = Finished normally
⊞ Stopped      = Terminated by user
✕ Error        = Crashed or failed
📡 Disconnected = Lost connection (wifi-off icon)
```

**Isolation Strategy:**
```
⚙️ Worktree    = GitBranch icon (purple)
⚙️ Clone       = Copy icon (purple)
(none) Existing = No icon, shows path
```

**Parent-Child Relationship:**
```
conductor  = Cyan badge (parent)
child      = Orange badge (child) + left-indented
```

### 7. Responsive Breakpoints
```
Mobile          Tablet          Desktop         Large
(<640px)        (640-1024px)    (1024-1280px)   (>1280px)
────────────────────────────────────────────────────
1 column        2 columns       3 columns       4 columns
sheet drawer    inline sidebar  inline sidebar  inline sidebar
compact labels  full labels     full labels     full labels
```

### 8. Key Interactions

**Search:**
```
[🔍 Search sessions…]
   ↓
Filters by: title, directory path, display name (substring)
Real-time, no debounce
```

**Sort:**
```
[Sort ▼]
├─ Recent (default)     → newest first
├─ Name                 → A-Z alphabetically
└─ Status               → busy → waiting → idle → running → ...
```

**Group:**
```
[Group ▼]
├─ Directory (default)
├─ Session Status
├─ Connection Status
├─ Source (isolation strategy)
└─ None (flat grid)
```

### 9. State Transitions

**Session Lifecycle:**
```
          start()
            ↓
    [running] ← ← resume()
      ↓ ↓ ↓
      ↓ ↓ └─→ [stopped] ← terminate()
      ↓ └────→ [completed] (finished)
      └──────→ [error] (crash)
                  ↓
        [disconnected] (lost connection)
```

**Activity Transitions (while running):**
```
[idle] ← → [busy] (agent working)
        ← → [waiting_input] (needs prompt)
```

### 10. Persistence (LocalStorage)

```
weave:fleet:prefs          = { groupBy, sortBy }
weave:fleet:collapsed      = [workspaceId, ...]
weave:sidebar:collapsed    = [workspaceId, ...]
weave:sidebar:pinned       = [workspaceId, ...]
weave:sidebar:hideInactive = boolean
model-override:[sessionId] = { providerID, modelID }
```

---

## Component Hierarchy

```
page.tsx (Fleet Dashboard)
├─ Header
│  └─ NewSessionButton
├─ SummaryBar
│  ├─ Zap (Active count)
│  ├─ Pause (Idle count)
│  ├─ Hash (Token count)
│  └─ ListTodo (Queued count)
├─ FleetToolbar
│  ├─ Search input
│  ├─ GroupBy dropdown
│  └─ SortBy dropdown
└─ Content (by grouping mode)
   ├─ GroupBy="directory" → SessionGroup[] (collapsible)
   │  └─ LiveSessionCard (grid)
   │     ├─ Link overlay
   │     ├─ Status dot
   │     ├─ Badges (status, isolation, parent/child)
   │     ├─ Action buttons (terminate, abort, resume, delete)
   │     └─ ContextMenu
   ├─ GroupBy="session-status" → Status section[]
   │  └─ LiveSessionCard[]
   ├─ GroupBy="connection-status" → Lifecycle section[]
   │  └─ LiveSessionCard[]
   ├─ GroupBy="source" → IsolationStrategy section[]
   │  └─ LiveSessionCard[]
   └─ GroupBy="none" → flat LiveSessionCard[]

Sidebar
├─ SidebarIconRail (60px, fixed icons)
│  ├─ Fleet icon
│  ├─ GitHub icon
│  ├─ Repositories icon
│  ├─ Pipelines icon
│  ├─ Queue icon
│  └─ Settings icon
└─ ContextualPanel (Fleet by default)
   ├─ Hide Inactive toggle + Remove button
   └─ SidebarWorkspaceItem[] (collapsible)
      ├─ Chevron (expand/collapse)
      ├─ Status dot (running?)
      ├─ Workspace name (inline-editable)
      ├─ Session count badge
      ├─ Overflow menu (new, open, terminate all)
      └─ SidebarSessionItem[] (nested by parent-child)
         ├─ Status dot
         ├─ Session name (truncated)
         ├─ Context menu (rename, fork, delete, etc.)
         └─ (child indicator if isChild)
```

---

## Color Palette (Status & Feedback)

```
Activity Status:
  Busy      = green-500 (pulsing)
  Idle      = muted-foreground/50

Lifecycle Status (icons):
  Running   = (no icon, or green dot)
  Completed = Square icon (gray)
  Stopped   = Square icon (gray)
  Error     = (red, context-dependent)
  Disconnected = WifiOff icon (red/orange)

Isolation Strategy (badges):
  Worktree/Clone = purple-600 dark:purple-400

Parent-Child:
  Parent    = cyan-600 dark:cyan-400 (conductor badge)
  Child     = orange-600 dark:orange-400

Actions:
  Hover     = foreground/20 border, shadow
  Terminate = hover:text-destructive (red)
  Abort     = hover:text-amber-500 (yellow)
  Resume    = hover:text-green-500 (green)
  Delete    = hover:text-red-500 (red)

Sections:
  Header    = text-muted-foreground (uppercase, small)
  Count     = badge-secondary
```

---

## Performance Metrics

- **Polling interval:** 15s (sessions), 30s (summary)
- **SSE events:** Real-time (activity_status, token_update)
- **Patch pruning:** Smart (only stale patches removed)
- **Deferred rendering:** Yes (workspace groups)
- **Memoization:** React.memo + useMemo on all cards + computations
- **Structural sharing:** Sessions array only updated if changed

---

## API Integration Points

```
GET  /api/sessions                      ← Polling (15s)
GET  /api/fleet/summary                 ← Summary (30s)
POST /api/sessions                      ← Create new session
POST /api/sessions/[id]/fork            ← Fork session
POST /api/sessions/[id]/resume          ← Resume stopped session
POST /api/sessions/[id]/abort           ← Interrupt busy session
POST /api/sessions/[id]?action=terminate ← Terminate session
DELETE /api/sessions/[id]               ← Permanently delete
PATCH /api/sessions/[id]                ← Rename (title)
PATCH /api/workspaces/[id]              ← Rename workspace
SSE /api/sessions/events                ← Real-time event stream
```

---

## Key Design Principles

1. **Information at a glance:** Status dots + count badges + color coding
2. **Responsive hierarchy:** Desktop: sidebar + grid; Mobile: drawer + full-width
3. **Quick actions:** Hover reveals buttons; right-click for context menu
4. **Real-time awareness:** SSE patches for immediate feedback
5. **Flexible organization:** Multiple grouping/sorting modes for different workflows
6. **Persistent state:** User preferences stored in localStorage
7. **Accessibility:** Keyboard navigation, ARIA labels, focus management
8. **Performance:** Polling + SSE + memoization for smooth interactions at scale
