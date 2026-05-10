# Architecture & Design Patterns — Weave Fleet Sessions UX

## System Architecture

### Three-Layer UI Stack

```
┌─────────────────────────────────────────────────────────┐
│ PRESENTATION LAYER                                      │
│ ┌──────────────────┬──────────────────┬────────────────┐│
│ │ Fleet Dashboard  │ Session Detail   │ Sidebar Nav    ││
│ │ (Grid + Metrics) │ (Stream + Tabs)  │ (Tree)         ││
│ └──────────────────┴──────────────────┴────────────────┘│
│                                                         │
├─────────────────────────────────────────────────────────┤
│ STATE MANAGEMENT LAYER                                  │
│ ┌──────────────────────────────────────────────────────┐│
│ │ SessionsContext (Redux-like)                         ││
│ │  • polled sessions (15s poll)                        ││
│ │  • SSE patches (real-time)                           ││
│ │  • optimistic renames (immediate)                    ││
│ │  • Smart patch pruning (when poll catches up)        ││
│ └──────────────────────────────────────────────────────┘│
│                                                         │
├─────────────────────────────────────────────────────────┤
│ DATA FETCHING LAYER                                     │
│ ┌──────────────────┬──────────────────┬────────────────┐│
│ │ useSessions()    │ useFleetSummary()│ useGlobalSSE() ││
│ │ (polling hook)   │ (polling hook)   │ (SSE singleton)││
│ └──────────────────┴──────────────────┴────────────────┘│
│                                                         │
├─────────────────────────────────────────────────────────┤
│ API LAYER                                               │
│ ┌──────────────────────────────────────────────────────┐│
│ │ REST endpoints + SSE stream                          ││
│ │ /api/sessions, /api/fleet/summary, /api/*/events   ││
│ └──────────────────────────────────────────────────────┘│
└─────────────────────────────────────────────────────────┘
```

### Data Flow: Session State

```
API Layer
   ↓ (poll /api/sessions)
useSessions() [15s]
   ↓ (memoized, structural sharing)
SessionsContext.polledSessions
   ↓ (merge with SSE patches)
useMemo() {
  sessions = polledSessions;
  for (patch in ssePatchesRef) {
    sessions = patchActivityStatus(sessions, patch);
  }
  return sessions;
}
   ↓
sessions (merged)
   ↓ (filtered, sorted, grouped)
page.tsx renderContent()
   ↓
Grid/WorkspaceGroup/Cards rendered

┌─ Parallel: SSE Stream
│   ↓ (event: activity_status)
│   ssePatchesRef.current = new Map(...)
│   requestAnimationFrame(() => setSseGeneration(n+1))
│   ↓
│   useMemo() re-runs
│   ↓
│   Patches applied (fast)
```

---

## State Management Pattern: Hybrid Polling + SSE

### Problem
- **Polling alone:** Stale data, network overhead
- **SSE alone:** May miss messages, complexity
- **Combined:** Can lead to inconsistent state (poll doesn't include SSE patches)

### Solution: SessionsContext Smart Patches

```typescript
// Refs for efficient mutations (no state overhead)
const ssePatchesRef = useRef<Map<sessionId, activityStatus>>();
const titlePatchesRef = useRef<Map<sessionId, title>>();
const tokenPatchesRef = useRef<Map<sessionId, {totalTokens, totalCost}>>();

// Batching via rAF
const rafRef = useRef<number | null>(null);

useEffect(() => {
  // On SSE event:
  ssePatchesRef.current.set(sessionId, newStatus);
  if (!rafRef.current) {
    rafRef.current = requestAnimationFrame(() => {
      setSseGeneration(n + 1);
      rafRef.current = null;
    });
  }
}, [sse]);

// Merge patches with poll results
const sessions = useMemo(() => {
  // Prune patches the poll has caught up with
  if (lastPolledRef.current !== polledSessions) {
    ssePatchesRef.current = pruneStalePatches(ssePatchesRef.current, polledSessions);
  }
  
  // Apply remaining patches
  let result = polledSessions;
  for (const [sessionId, status] of ssePatchesRef.current) {
    result = patchActivityStatus(result, sessionId, status);
  }
  return result;
}, [polledSessions, sseGeneration]);
```

### Advantages
1. **Immediate feedback:** Activity updates feel instant
2. **Consistency:** Poll is source of truth; SSE only ahead temporarily
3. **Memory efficient:** Patches stored in refs, not state
4. **Batching:** rAF prevents re-render spam
5. **Smart cleanup:** Patches pruned when no longer needed

---

## Data Transformation Pipeline

### Step 1: Fetch Sessions
```typescript
API response: SessionListItem[]
```

### Step 2: Apply SSE Patches
```typescript
// In SessionsContext
for (patch of ssePatchesRef.current) {
  sessions = patchActivityStatus(sessions, patch.sessionId, patch.status);
}
```

### Step 3: Filter by Workspace
```typescript
// In page.tsx
const workspaceFiltered = filterSessionsByWorkspace(sessions, workspaceFilter);
```

### Step 4: Filter by Search
```typescript
const searchFiltered = useMemo(() => {
  const q = search.trim().toLowerCase();
  return workspaceFiltered.filter((s) => {
    return title.includes(q) || dir.includes(q) || displayName.includes(q);
  });
}, [workspaceFiltered, search]);
```

### Step 5: Sort
```typescript
const sortSessions = (items) => {
  const sorted = [...items];
  if (prefs.sortBy === 'recent') {
    sorted.sort((a, b) => b.session.time.created - a.session.time.created);
  }
  // ... other sorts
  return sorted;
};
```

### Step 6: Group
```typescript
const deferredWorkspaceGroups = useDeferredValue(
  allWorkspaces.map(g => ({ ...g, sessions: sortSessions(g.sessions) }))
);
```

### Step 7: Render
```typescript
deferredWorkspaceGroups.map(group => (
  <SessionGroup key={group.workspaceDirectory} group={group} />
))
```

---

## Component Composition Patterns

### Pattern 1: Collapsible Container with Header

```typescript
// SessionGroup
<Collapsible open={!isCollapsed} onOpenChange={handleOpenChange}>
  <div className="flex items-center gap-2 py-1.5">
    <CollapsibleTrigger>
      <ChevronRight className={cn("rotate-90": !isCollapsed)} />
    </CollapsibleTrigger>
    <span className="h-2 w-2 rounded-full" />  {/* Status dot */}
    <InlineEdit value={displayName} onSave={handleRename} />
    <Badge>{sessionCount}</Badge>
    <DropdownMenu>...</DropdownMenu>
  </div>
  
  <CollapsibleContent>
    {/* SessionList or LiveSessionCard[] */}
  </CollapsibleContent>
</Collapsible>
```

**Reused in:**
- `SessionGroup` (fleet page)
- `SidebarWorkspaceItem` (sidebar)

### Pattern 2: Card with Hover Actions

```typescript
// LiveSessionCard
<div className="relative group">
  <Link href={sessionPath}>
    <Card className="hover:border-foreground/20 cursor-pointer">
      {/* Card content */}
    </Card>
  </Link>
  
  {canTerminate && (
    <Button
      className="absolute top-2 right-2 opacity-0 group-hover:opacity-100"
      onClick={(e) => { e.preventDefault(); onTerminate(); }}
    >
      <Trash2 />
    </Button>
  )}
  {canAbort && ( /* similar */ )}
  {canResume && ( /* similar */ )}
</div>
```

**Advantages:**
- Single interactive element (link to detail)
- Action buttons overlay on hover
- `e.preventDefault()` + `e.stopPropagation()` prevent navigation

### Pattern 3: Inline Edit

```typescript
// SidebarSessionItem, SidebarWorkspaceItem
<InlineEdit
  value={name}
  onSave={async (newName) => {
    patchOptimistically(newName);
    await api.rename(id, newName);
  }}
  editing={isRenaming}
  onEditingChange={setIsRenaming}
  className="text-xs truncate"
/>
```

**Behavior:**
1. Click to edit (or F2 key)
2. Input appears inline
3. Enter to save, Esc to cancel
4. Optimistic update + server confirmation

### Pattern 4: Multi-Select via Context Menu

```typescript
<ContextMenu>
  <ContextMenuTrigger>
    <WorkspaceItem />
  </ContextMenuTrigger>
  <ContextMenuContent>
    <ContextMenuItem onClick={handleRename}>Rename</ContextMenuItem>
    <ContextMenuItem onClick={handlePin}>Pin</ContextMenuItem>
    <ContextMenuSeparator />
    <ContextMenuItem onClick={handleTerminateAll}>Terminate All</ContextMenuItem>
  </ContextMenuContent>
</ContextMenu>
```

---

## Performance Optimization Strategies

### 1. Structural Sharing (Referential Equality)

```typescript
// In useSessions hook
setSessions(prev => 
  // Only return new reference if data changed
  sessionsChanged(prev, newData) ? newData : prev
);
```

**Effect:** React.memo children don't re-render if parent reference unchanged.

### 2. Memoization

```typescript
export const LiveSessionCard = React.memo(function LiveSessionCard(...) {
  // Component definition
});

// In parent:
<LiveSessionCard item={item} /> // Only re-renders if `item` reference changes
```

### 3. useCallback for Stable Handlers

```typescript
const handleTerminate = useCallback(
  (sessionId, instanceId) => {
    // ...
  },
  [terminateSession, refetch]  // Stable if deps unchanged
);

// Pass to memoized child
<LiveSessionCard onTerminate={handleTerminate} />
```

### 4. useMemo for Expensive Computations

```typescript
const workspaceGroups = useMemo(() => {
  return groupSessionsByWorkspace(sessions);  // O(n log n) sort
}, [sessions]);  // Only recompute if sessions reference changes
```

### 5. useDeferredValue for Batch Rendering

```typescript
const deferredGroups = useDeferredValue(workspaceGroups);

// If SSE patch arrives, React prioritizes user input (search, click)
// over re-rendering workspace groups (lower priority)
```

### 6. requestAnimationFrame for Batching

```typescript
useEffect(() => {
  function handleSSE(event) {
    patch();
    if (!rafRef.current) {
      rafRef.current = requestAnimationFrame(() => {
        setSseGeneration(n + 1);  // Single state update per frame
        rafRef.current = null;
      });
    }
  }
}, []);
```

### 7. Lazy Rendering (Collapsible Groups)

```typescript
// Sidebar: only render expanded workspaces' sessions
{!isCollapsed && (
  <CollapsibleContent>
    {/* Only rendered when expanded */}
  </CollapsibleContent>
)}
```

---

## State Persistence Layer

### LocalStorage Keys

```
weave:fleet:prefs = {
  groupBy: "directory" | "session-status" | ...,
  sortBy: "recent" | "name" | "status"
}

weave:fleet:collapsed = ["workspace-id-1", "workspace-id-2", ...]
weave:sidebar:collapsed = ["workspace-id-1", ...]
weave:sidebar:pinned = ["workspace-id-1", ...]
weave:sidebar:hideInactive = true | false

model-override:[sessionId] = { providerID, modelID }
```

### Custom Hook: useSyncExternalStore Pattern

```typescript
// usePersistedState wraps useSyncExternalStore
export function usePersistedState<T>(key, defaultValue) {
  return useSyncExternalStore(
    subscribe: (listener) => {
      window.addEventListener('storage', listener);
      return () => window.removeEventListener('storage', listener);
    },
    getSnapshot: () => localStorage.getItem(key),
    getServerSnapshot: () => defaultValue
  );
}
```

**Advantages:**
- SSR-safe (server renders default)
- Client hydrates from localStorage
- External store (localStorage) is source of truth

---

## Error Handling & Recovery

### Pattern 1: API Error with User Feedback

```typescript
const { terminateSession } = useTerminateSession();

const handleTerminate = useCallback(async () => {
  try {
    await terminateSession(sessionId, instanceId);
    refetch();  // Manual refetch
  } catch (error) {
    // Error surfaced inside useTerminateSession (via toast/context)
    // Component doesn't handle directly
  }
}, [...]);
```

**Responsibility split:**
- Hook handles API call + error display
- Component coordinates workflows

### Pattern 2: Graceful Degradation (SSE Failures)

```typescript
// If SSE connection dies, polling continues
// Patches may be stale, but users see updated data on next poll

if (error) return <ErrorBoundary message="Failed to load sessions" />;
if (!polledSessions && isLoading) return <Spinner />;
```

### Pattern 3: Optimistic Updates with Rollback

```typescript
// Rename example
const handleRename = useCallback(async (newName) => {
  const prevName = item.session.title;
  
  // 1. Optimistic patch
  patchSessionTitle(item.session.id, newName);
  
  try {
    // 2. Server API call
    await renameSession(dbId, newName, refetch);
  } catch (error) {
    // 3. Rollback on failure
    patchSessionTitle(item.session.id, prevName);
    // Error displayed by hook
  }
}, []);
```

---

## Accessibility Implementation

### Keyboard Navigation (Sidebar Tree)

```typescript
const handleTreeKeyDown = (e: KeyboardEvent) => {
  const items = tree.querySelectorAll("[role='treeitem'], [data-tree-leaf]");
  const currentIndex = items.indexOf(document.activeElement);
  
  switch (e.key) {
    case 'ArrowDown':
      e.preventDefault();
      items[currentIndex + 1]?.focus();
      break;
    case 'ArrowUp':
      e.preventDefault();
      items[currentIndex - 1]?.focus();
      break;
    case 'ArrowRight':
      e.preventDefault();
      // If collapsed, expand; else move to first child
      break;
    case 'ArrowLeft':
      e.preventDefault();
      // If expanded, collapse; else move to parent
      break;
    case 'Enter':
      e.preventDefault();
      document.activeElement?.click();  // Navigate to session
      break;
  }
};
```

### ARIA Labels

```typescript
<div
  role="treeitem"
  aria-label="my-project workspace"
  tabIndex={0}
  onKeyDown={handleKeyDown}
>
  <CollapsibleTrigger aria-label="Expand my-project">
    <ChevronRight />
  </CollapsibleTrigger>
  
  <div role="group">
    {/* Child tree items */}
  </div>
</div>
```

### Focus Management

```typescript
className="focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
```

Provides visible outline when keyboard focused (not mouse).

---

## Scaling Considerations

### For 100+ Sessions

1. **Virtualization:** Use `react-window` for very long lists
   - Only render visible items
   - Significant performance gains

2. **Pagination:** Load sessions in batches (20 at a time)
   - Lazy load on scroll
   - Keeps initial bundle small

3. **Debounced Search:** Add debounce to search input
   - Prevent filter on every keystroke
   - Batch API calls

4. **Worker Thread:** Move heavy computations off main thread
   - `groupSessionsByWorkspace()` in worker
   - Sorting/filtering in worker
   - Worker posts results back

### For Real-Time Updates at Scale

1. **Selective SSE:** Only subscribe to relevant sessions
   - Filter by workspace before SSE subscription
   - Reduces event volume

2. **Compression:** Delta-compress session patches
   - Only send changed fields
   - Smaller bandwidth

3. **Caching Strategy:**
   - IndexedDB for historical data
   - Session detail cache (hour TTL)
   - Workspace list cache (5 min TTL)

---

## Testing Strategy

### Unit Tests
```typescript
// lib/session-utils.ts
describe('nestSessions', () => {
  it('groups child sessions under parents', () => {
    const items = [
      { session: { id: 'p1' }, dbId: 'p1', parentSessionId: null },
      { session: { id: 'c1' }, dbId: null, parentSessionId: 'p1' },
    ];
    const nested = nestSessions(items);
    expect(nested[0].children).toHaveLength(1);
  });
});
```

### Component Tests
```typescript
describe('LiveSessionCard', () => {
  it('navigates to detail on click', () => {
    render(<LiveSessionCard item={mockSession} />);
    expect(screen.getByRole('link')).toHaveAttribute(
      'href',
      `/sessions/${mockSession.session.id}?instanceId=${mockSession.instanceId}`
    );
  });
  
  it('shows terminate button when running', () => {
    render(<LiveSessionCard item={mockRunningSession} />);
    expect(screen.getByTitle('Terminate session')).toBeInTheDocument();
  });
});
```

### Integration Tests
```typescript
describe('Fleet Page', () => {
  it('filters sessions by workspace', async () => {
    render(<FleetPage />);
    // Click workspace in sidebar
    // Verify URL changed to /?workspace=...
    // Verify grid shows only sessions from that workspace
  });
});
```

---

## Security Considerations

### 1. Session IDs in URLs
```typescript
// Always encode (URL encode) session IDs (UUID v4 safe, but best practice)
href={`/sessions/${encodeURIComponent(sessionId)}`}
```

### 2. Sensitive Data in localStorage
```typescript
// ✓ OK: user preferences, collapsed state, pinning
localStorage.setItem('weave:fleet:prefs', JSON.stringify(prefs));

// ✗ NOT OK: auth tokens, API keys
// Use httpOnly cookies instead
```

### 3. XSS Prevention
```typescript
// ✓ React JSX prevents injection by default
<span>{session.title}</span>

// ✗ DON'T:
<span dangerouslySetInnerHTML={{__html: session.title}} />
```

---

## Future Enhancements

1. **Bulk Actions:** Select multiple sessions → terminate all, delete all
2. **Favorites:** Pin/star favorite sessions for quick access
3. **Tags:** Label sessions (e.g., "customer", "urgent", "review")
4. **Export:** Export session history (CSV, JSON)
5. **Webhooks:** Notify on session completion (Slack, Discord)
6. **Advanced Filtering:** Filter by activity, cost, duration, tags
7. **Analytics:** Dashboard showing session trends, cost breakdown
8. **Undo/Redo:** Undo destructive actions (delete, terminate)
9. **Comparison:** Side-by-side compare two session timelines
10. **Session Templates:** Save session config as template for repeated tasks

---

## Conclusion

The Weave Fleet sessions UX is built on solid architectural foundations:

- **Hybrid polling + SSE** for reliability + responsiveness
- **Composable React patterns** (collapsibles, inline edits, hovers)
- **Smart performance** (memoization, deferred rendering, structural sharing)
- **Persistent state** (localStorage with useSyncExternalStore)
- **Accessibility** (keyboard navigation, ARIA, focus management)
- **Scalable** (ready for 100+, 1000+ sessions with optimizations)

The design prioritizes **user control** (multiple groupings, sorts, filters) while keeping **information density** high (status dots, badges, metrics). The system is **resilient** to network hiccups and gracefully handles edge cases (no sessions, loading, errors).
