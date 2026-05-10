# Weave Agent Fleet — Sessions UX Exploration Index

**Status:** Complete ✅  
**Generated:** May 10, 2026  
**Total Documentation:** 3 files, ~66KB

---

## 📋 Documentation Structure

### 1. **SESSIONS_UX_EXPLORATION.md** (Comprehensive Deep-Dive)
**Size:** 32.7 KB | **16 major sections**

The primary reference document covering the entire Sessions UX design:

- **Section 1-7:** UI Components & Patterns
  - Session list/panel layout (fleet page + sidebar)
  - Session card details & interaction zones
  - Grouping strategies (5 modes: directory, status, connection, source, none)
  - Workspace concept & parent-child hierarchy
  - Session actions (terminate, abort, resume, delete, fork)
  - Session states & visual indicators

- **Section 8-10:** Navigation & Fleet Management
  - How clicking navigates (fleet → detail page)
  - Fleet dashboard & summary bar metrics
  - Session filtering (search, workspace filter, sorting)

- **Section 11-16:** Advanced Topics
  - Real-time updates & state management (polling + SSE)
  - Responsive design & mobile optimization
  - Component tree & file locations
  - UX patterns & best practices
  - Types & data structures
  - Notable implementation details (nesting, merging, isolation strategies)

**Use this for:**
- Complete understanding of every feature
- Learning about data flows & state management
- Finding specific component implementations

---

### 2. **SESSIONS_UX_VISUAL_REFERENCE.md** (Quick Visual Guide)
**Size:** 13.6 KB | **Visual ASCII diagrams**

Fast-reference guide with visual representations:

- **Layout diagrams:** Fleet dashboard, sidebar tree, session cards
- **Status indicators legend:** Activity, lifecycle, isolation, parent-child
- **Responsive breakpoints:** 1-4 column layouts
- **Interaction flows:** Search, sort, group, state transitions
- **Component hierarchy:** Tree of all UI elements
- **Color palette:** Status colors, hover states, badges
- **Performance metrics:** Polling intervals, SSE batching
- **API endpoints:** All REST + SSE routes

**Use this for:**
- Quick visual refresh on layouts
- Understanding status indicator meanings
- Learning component relationships at a glance
- Checking responsive breakpoints

---

### 3. **SESSIONS_UX_ARCHITECTURE.md** (Design Patterns & Internals)
**Size:** 19.7 KB | **Technical deep-dive**

System architecture and implementation patterns:

- **System architecture:** 4-layer stack (presentation → state → data → API)
- **Hybrid polling + SSE pattern:** How real-time updates work
- **Data transformation pipeline:** Filter → search → sort → group → render
- **Component patterns:**
  - Collapsible container with header
  - Card with hover actions
  - Inline edit
  - Multi-select via context menu
- **Performance optimizations:** 7 strategies (structural sharing, memoization, etc.)
- **State persistence:** LocalStorage keys & useSyncExternalStore
- **Error handling & recovery:** 3 patterns (API errors, graceful degradation, optimistic updates)
- **Accessibility implementation:** Keyboard navigation, ARIA, focus management
- **Scaling considerations:** 100+ sessions, virtualization, workers
- **Testing strategy:** Unit, component, integration tests
- **Security & future enhancements:** 10 planned features

**Use this for:**
- Understanding architectural decisions
- Learning component composition patterns
- Scaling strategies for many sessions
- Performance optimization techniques

---

## 🎯 Quick Navigation by Topic

### Understanding the UI
→ Start with **SESSIONS_UX_EXPLORATION.md** sections 1-3  
→ Then review **SESSIONS_UX_VISUAL_REFERENCE.md** layouts

### Data Flow & State Management
→ **SESSIONS_UX_EXPLORATION.md** section 8  
→ **SESSIONS_UX_ARCHITECTURE.md** "System Architecture" & "Hybrid polling + SSE"

### Building Similar Features
→ **SESSIONS_UX_ARCHITECTURE.md** "Component Composition Patterns"  
→ **SESSIONS_UX_EXPLORATION.md** section 14-15 (Types & Implementation Details)

### Performance at Scale
→ **SESSIONS_UX_ARCHITECTURE.md** "Scaling Considerations" & "Performance Optimization Strategies"  
→ **SESSIONS_UX_VISUAL_REFERENCE.md** "Performance Metrics"

### Responsive Design
→ **SESSIONS_UX_EXPLORATION.md** section 9  
→ **SESSIONS_UX_VISUAL_REFERENCE.md** "Responsive Breakpoints"

### Real-Time Updates
→ **SESSIONS_UX_EXPLORATION.md** section 8  
→ **SESSIONS_UX_ARCHITECTURE.md** "Hybrid Polling + SSE Pattern"

### Accessibility
→ **SESSIONS_UX_ARCHITECTURE.md** "Accessibility Implementation"  
→ **SESSIONS_UX_EXPLORATION.md** section 16

---

## 📚 Key Findings Summary

### 1. **Session List Display**
- **Primary:** Grid layout (1-4 columns based on screen size)
- **Sidebar:** Tree layout (workspace groups collapsible, sessions nested)
- **Card content:** Status dot, title, badges, timestamp, token count, actions
- **Link behavior:** Each card navigates to `/sessions/[id]?instanceId=[id]`

### 2. **Session Grouping**
- **5 modes:** Directory (default), Session Status, Connection Status, Source, None
- **Directory grouping:** Workspaces merged by path, nested sessions under parents
- **Persistent preferences:** Stored in localStorage as `weave:fleet:prefs`

### 3. **Session Actions**
- **Visible on hover:** Terminate, Abort, Resume, Delete buttons
- **Via context menu:** Rename, Fork, Open in IDE, Copy path, Delete
- **Workspace actions:** New session, Open, Terminate All, Remove Inactive
- **Sidebar actions:** Same as cards, plus Pin/Unpin

### 4. **Session States**
- **Activity:** Busy (green pulsing dot) ↔ Idle (gray dot)
- **Lifecycle:** Running → Completed/Stopped/Error/Disconnected
- **Visual:** Status badges, connection icons (wifi-off, stop square), opacity fade for inactive
- **Icons:** Worktree (branch), Clone (copy), Conductor (cyan badge), Child (orange badge)

### 5. **Navigation**
- **Fleet page → Detail:** Click card → `/sessions/[id]`
- **Sidebar filter:** Click workspace → `/?workspace=[id]`
- **Sidebar → Detail:** Click session → `/sessions/[id]`
- **Back:** Browser history or click fleet icon

### 6. **Fleet Dashboard**
- **Summary bar:** 4 metrics (Active, Idle, Tokens, Queued) with icons
- **Toolbar:** Search, Group selector, Sort selector
- **Empty states:** Context-specific messages ("No sessions running", etc.)
- **Loading:** Spinner during initial fetch

### 7. **Filtering & Search**
- **Search:** Real-time substring match on title, directory, display name
- **Workspace filter:** Via URL parameter `?workspace=[id]`
- **Sort options:** Recent (default), Name, Status
- **Sort order:** Applies within grouping boundaries

### 8. **Real-Time Updates**
- **Polling:** 15s interval (`useSessions()`)
- **SSE:** Real-time `activity_status` + `token_update` events
- **Strategy:** Poll is source of truth; SSE patches applied optimistically
- **Batching:** rAF for single re-render per frame
- **Smart pruning:** Patches cleared when poll catches up

### 9. **Responsive Design**
- **Desktop (>1024px):** Inline sidebar + 3-4 column grid
- **Tablet (640-1024px):** Inline sidebar + 2 column grid
- **Mobile (<640px):** Sheet drawer sidebar + 1 column grid
- **Touch:** Action buttons always visible (no hover), slightly larger

### 10. **Performance**
- **Structural sharing:** Only update if data changed
- **Memoization:** React.memo + useMemo on all cards + computations
- **Deferred rendering:** `useDeferredValue()` for workspace groups
- **Lazy collapse:** Only render visible sections
- **Ready for scale:** Virtualization + pagination planned for 100+ sessions

---

## 📍 File Reference

| File | Purpose | Sections |
|------|---------|----------|
| **SESSIONS_UX_EXPLORATION.md** | Complete reference | 1-16 |
| **SESSIONS_UX_VISUAL_REFERENCE.md** | Visual guide | Layouts, diagrams, legends |
| **SESSIONS_UX_ARCHITECTURE.md** | Technical deep-dive | Architecture, patterns, internals |

---

## 🔍 Code Locations (Key Files)

### Pages & Routes
- `src/app/page.tsx` — Fleet dashboard
- `src/app/sessions/[id]/page.tsx` — Session detail

### Layout & Sidebar
- `src/components/layout/sidebar.tsx` — Main sidebar (icon rail + panel)
- `src/components/layout/fleet-panel.tsx` — Fleet sidebar content
- `src/components/layout/sidebar-workspace-item.tsx` — Collapsible workspace
- `src/components/layout/sidebar-session-item.tsx` — Session in sidebar

### Fleet Grid & Cards
- `src/components/fleet/fleet-toolbar.tsx` — Search, group, sort controls
- `src/components/fleet/live-session-card.tsx` — Grid/flat session card
- `src/components/fleet/session-group.tsx` — Collapsible workspace group
- `src/components/fleet/summary-bar.tsx` — 4-metric dashboard
- `src/components/fleet/confirm-delete-session-dialog.tsx` — Delete confirmation

### State Management
- `src/contexts/sessions-context.tsx` — Polling + SSE + optimistic patches
- `src/contexts/sidebar-context.tsx` — Panel width, collapse state

### Hooks
- `src/hooks/use-workspaces.ts` — Group sessions by workspace
- `src/hooks/use-sessions.ts` — Poll /api/sessions
- `src/hooks/use-*-session.ts` — 6 hooks for session actions

### Utilities
- `src/lib/types.ts` — Core type definitions
- `src/lib/api-types.ts` — API request/response shapes
- `src/lib/session-utils.ts` — nestSessions(), isInactiveSession()
- `src/lib/workspace-utils.ts` — groupSessionsByWorkspace(), WorkspaceGroup

---

## ✨ Notable Design Decisions

1. **Hybrid Polling + SSE**
   - Best of both worlds: reliable polling + responsive SSE
   - Smart patch pruning prevents inconsistency

2. **Collapsible Workspace Groups**
   - Scales to many workspaces without overwhelming UI
   - Collapse state persisted per user

3. **Inline Editing**
   - Rename sessions/workspaces without modal
   - Optimistic update + server confirmation

4. **Multiple Grouping Modes**
   - Different workflows prefer different views
   - Easy to switch modes without losing state

5. **Parent-Child Session Hierarchy**
   - Support for "conductor" pattern (orchestration)
   - Visual distinction (badges + indentation)

6. **Deferred Rendering**
   - SSE updates don't block user interactions
   - Workspace groups render with slight delay

7. **Responsive Mobile-First**
   - Desktop: full sidebar + grid
   - Mobile: sheet drawer + full-width
   - Consistent interactions across sizes

---

## 🚀 Future Reference

### When Building New Features
1. **Check patterns:** SESSIONS_UX_ARCHITECTURE.md → Component Composition Patterns
2. **Check types:** SESSIONS_UX_EXPLORATION.md section 14
3. **Check performance:** SESSIONS_UX_ARCHITECTURE.md → Performance Optimization
4. **Check accessibility:** SESSIONS_UX_ARCHITECTURE.md → Accessibility Implementation

### When Optimizing Performance
- Virtualization: `react-window` for 100+ items
- Worker threads: Move `groupSessionsByWorkspace()` to worker
- Debounced search: Add 300ms debounce to search input
- IndexedDB cache: Historical data + workspace list

### When Scaling to 1000+ Sessions
- Pagination: Load 20-50 at a time
- Selective SSE: Subscribe only to visible workspaces
- Worker threads: Offload sorting/filtering
- Delta updates: Only send changed fields in SSE events

---

## 📞 Key Contacts in Code

**Component owners** (per section):
- Fleet dashboard: `src/app/page.tsx` (main orchestrator)
- Session cards: `src/components/fleet/live-session-card.tsx`
- Workspace grouping: `src/components/fleet/session-group.tsx` + `src/hooks/use-workspaces.ts`
- Sidebar: `src/components/layout/` (entire directory)
- State management: `src/contexts/sessions-context.tsx`
- Utils: `src/lib/session-utils.ts`, `src/lib/workspace-utils.ts`

---

## 📊 Statistics

- **Total documentation:** 3 files, ~66 KB
- **Code sections explored:** 40+ files
- **Components analyzed:** 10 major components
- **Design patterns:** 4 identified
- **Visual diagrams:** 10+ ASCII diagrams
- **API endpoints:** 8 REST + 1 SSE
- **Feature areas:** 7 (list, grouping, actions, states, navigation, dashboard, filtering)
- **Performance optimizations:** 7 strategies documented

---

## ✅ Exploration Checklist

- [x] Session list/panel layout
- [x] Session cards (display + actions)
- [x] Session grouping (5 modes)
- [x] Workspace concept
- [x] Parent-child hierarchy
- [x] Session actions (all 7 types)
- [x] Session states (activity + lifecycle)
- [x] Visual indicators (dots, badges, icons)
- [x] Navigation flows
- [x] Fleet dashboard + summary bar
- [x] Search + filtering + sorting
- [x] Real-time updates (polling + SSE)
- [x] State management (SessionsContext)
- [x] Responsive design
- [x] Mobile optimization
- [x] Component tree
- [x] Performance optimizations
- [x] Accessibility
- [x] Data structures & types
- [x] Implementation details
- [x] Design patterns
- [x] Scaling strategies
- [x] Error handling
- [x] Security considerations
- [x] Testing strategy

---

**Exploration Complete!** 🎉

All aspects of the Weave Agent Fleet Sessions UX have been thoroughly explored and documented. Refer to the three documentation files for specific details, patterns, and implementation guidance.
