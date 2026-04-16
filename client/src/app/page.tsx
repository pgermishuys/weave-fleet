
import { useCallback, useMemo, useState, useDeferredValue, Suspense } from "react";
import { useNavigate, useSearchParams } from "react-router";
import { Header, NewSessionButton } from "@/components/layout/header";
import { SummaryBar } from "@/components/fleet/summary-bar";
import { FleetToolbar } from "@/components/fleet/fleet-toolbar";
import type { GroupBy, RetentionFilter, SortBy } from "@/components/fleet/fleet-toolbar";
import { SessionGroup } from "@/components/fleet/session-group";
import { LiveSessionCard } from "@/components/fleet/live-session-card";
import { useFoldableScreen } from "@/hooks/use-foldable-screen";
import { useSessionsContext } from "@/contexts/sessions-context";
import { useTerminateSession } from "@/hooks/use-terminate-session";
import { useAbortSession } from "@/hooks/use-abort-session";
import { useResumeSession } from "@/hooks/use-resume-session";
import { useDeleteSession } from "@/hooks/use-delete-session";
import { useArchiveSession } from "@/hooks/use-archive-session";
import { useUnarchiveSession } from "@/hooks/use-unarchive-session";
import { useOpenDirectory } from "@/hooks/use-open-directory";
import type { OpenTool } from "@/hooks/use-open-directory";

import { useWorkspaces } from "@/hooks/use-workspaces";
import { usePersistedState } from "@/hooks/use-persisted-state";
import { filterSessionsByWorkspace } from "@/lib/workspace-utils";
import { nestSessions } from "@/lib/session-utils";
import { ConfirmDeleteSessionDialog } from "@/components/fleet/confirm-delete-session-dialog";
import type { FleetSummary } from "@/lib/types";
import type { SessionListItem } from "@/lib/api-types";
import { Loader2 } from "lucide-react";

function FleetPageInner() {
  const {
    sessions,
    retentionFilter,
    setRetentionFilter,
    isLoading,
    error,
    refetch,
    summary: liveSummary,
  } = useSessionsContext();
  const { terminateSession } = useTerminateSession();
  const { abortSession } = useAbortSession();
  const { resumeSession, resumingSessionId } = useResumeSession();
  const { deleteSession, isDeleting } = useDeleteSession();
  const { archiveSession } = useArchiveSession();
  const { unarchiveSession } = useUnarchiveSession();
  const { openDirectory } = useOpenDirectory();
  const { isFolded } = useFoldableScreen();
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();
  const workspaceFilter = searchParams.get("workspace");

  const [deleteTarget, setDeleteTarget] = useState<{
    sessionId: string;
    instanceId: string;
    title: string;
  } | null>(null);

  // SSR-safe persisted prefs — useSyncExternalStore under the hood
  // ensures server renders defaults and client hydrates from localStorage.
  const [prefs, setPrefs] = usePersistedState<{ groupBy: GroupBy; sortBy: SortBy }>(
    "weave:fleet:prefs",
    { groupBy: "directory", sortBy: "recent" }
  );
  const [search, setSearch] = useState("");

  const handleGroupByChange = useCallback((groupBy: GroupBy) => {
    setPrefs((prev) => ({ ...prev, groupBy }));
  }, [setPrefs]);

  const handleSortByChange = useCallback((sortBy: SortBy) => {
    setPrefs((prev) => ({ ...prev, sortBy }));
  }, [setPrefs]);

  const handleRetentionFilterChange = useCallback((nextRetentionFilter: RetentionFilter) => {
    setRetentionFilter(nextRetentionFilter);
  }, [setRetentionFilter]);

  const handleTerminate = useCallback(async (sessionId: string, instanceId: string) => {
    try {
      await terminateSession(sessionId, instanceId);
      refetch();
    } catch {
      // error surfaced inside useTerminateSession
    }
  }, [terminateSession, refetch]);

  const handleAbort = useCallback(async (sessionId: string, instanceId: string) => {
    try {
      await abortSession(sessionId, instanceId);
    } catch {
      // error surfaced inside useAbortSession
    }
  }, [abortSession]);

  const handleResume = useCallback(async (sessionId: string) => {
    try {
      const result = await resumeSession(sessionId);
      navigate(
        `/sessions/${encodeURIComponent(result.session.id)}?instanceId=${encodeURIComponent(result.instanceId)}`
      );
    } catch {
      // error surfaced inside useResumeSession
      refetch();
    }
  }, [resumeSession, navigate, refetch]);

  const handleDeleteRequest = useCallback((sessionId: string, instanceId: string) => {
    const item = sessions.find((s) => s.session.id === sessionId);
    setDeleteTarget({
      sessionId,
      instanceId,
      title: item?.session.title || sessionId.slice(0, 12),
    });
  }, [sessions]);

  const handleArchive = useCallback(async (sessionId: string) => {
    try {
      await archiveSession(sessionId);
      refetch();
    } catch {
      // error surfaced inside useArchiveSession
    }
  }, [archiveSession, refetch]);

  const handleUnarchive = useCallback(async (sessionId: string) => {
    try {
      await unarchiveSession(sessionId);
      refetch();
    } catch {
      // error surfaced inside useUnarchiveSession
    }
  }, [unarchiveSession, refetch]);

  const handleDeleteConfirm = useCallback(async () => {
    if (!deleteTarget) return;
    try {
      await deleteSession(deleteTarget.sessionId, deleteTarget.instanceId);
      refetch();
    } catch {
      // error surfaced inside useDeleteSession
    } finally {
      setDeleteTarget(null);
    }
  }, [deleteTarget, deleteSession, refetch]);

  const handleOpen = useCallback((directory: string, tool: OpenTool) => {
    openDirectory(directory, tool);
  }, [openDirectory]);

  // Apply workspace URL filter — resolves workspaceId to directory so that all
  // sessions sharing the same workspace directory are included.
  const workspaceFiltered = useMemo(
    () => filterSessionsByWorkspace(sessions, workspaceFilter),
    [sessions, workspaceFilter]
  );

  const fleetVisibleSessions = useMemo(
    () => workspaceFiltered.filter((session) => !session.isHidden),
    [workspaceFiltered]
  );

  // Apply search filter
  const searchFiltered = useMemo(() => {
    const q = search.trim().toLowerCase();
    if (!q) return fleetVisibleSessions;
    return fleetVisibleSessions.filter((s) => {
      const title = s.session.title?.toLowerCase() ?? "";
      const dir = (s.sourceDirectory ?? s.workspaceDirectory).toLowerCase();
      const displayName = s.workspaceDisplayName?.toLowerCase() ?? "";
      return title.includes(q) || dir.includes(q) || displayName.includes(q);
    });
  }, [fleetVisibleSessions, search]);

  // Apply sort within session arrays
  const sortSessions = useCallback((items: SessionListItem[]): SessionListItem[] => {
    const sorted = [...items];
    if (prefs.sortBy === "recent") {
      sorted.sort((a, b) => b.session.time.created - a.session.time.created);
    } else if (prefs.sortBy === "name") {
      sorted.sort((a, b) => {
        const aTitle = a.session.title ?? a.session.id;
        const bTitle = b.session.title ?? b.session.id;
        return aTitle.localeCompare(bTitle);
      });
    } else if (prefs.sortBy === "status") {
      const activityOrder: Record<string, number> = { busy: 0, waiting_input: 1, idle: 2 };
      const lifecycleOrder: Record<string, number> = { running: 0, completed: 1, stopped: 2, error: 3, disconnected: 4 };
      sorted.sort((a, b) => {
        const aLifecycle = lifecycleOrder[a.lifecycleStatus] ?? 4;
        const bLifecycle = lifecycleOrder[b.lifecycleStatus] ?? 4;
        if (aLifecycle !== bLifecycle) return aLifecycle - bLifecycle;
        const aActivity = activityOrder[a.activityStatus ?? "idle"] ?? 3;
        const bActivity = activityOrder[b.activityStatus ?? "idle"] ?? 3;
        return aActivity - bActivity;
      });
    }
    return sorted;
  }, [prefs.sortBy]);

  // Derive workspace groups from filtered sessions
  const allWorkspaces = useWorkspaces(searchFiltered);

  // Memoize sorted workspace groups to preserve object identity for React.memo
  const sortedWorkspaceGroups = useMemo(
    () => allWorkspaces.map((g) => ({ ...g, sessions: sortSessions(g.sessions) })),
    [allWorkspaces, sortSessions]
  );

  // Defer workspace group rendering so rapid SSE updates don't synchronously
  // block the UI thread — groups will update with a slight delay instead.
  const deferredWorkspaceGroups = useDeferredValue(sortedWorkspaceGroups);

  const liveCount = liveSummary?.activeSessions ?? sessions.filter((s) => s.activityStatus === "busy").length;

  const summary: FleetSummary = {
    activeSessions: liveSummary?.activeSessions ?? liveCount,
    idleSessions: liveSummary?.idleSessions ?? sessions.filter((s) => s.lifecycleStatus === "running" && s.activityStatus === "idle").length,
    totalTokens: liveSummary?.totalTokens ?? 0,
    totalCost: liveSummary?.totalCost ?? 0,
    queuedTasks: 0,
  };

  const subtitle =
    sessions.length > 0
      ? `${sessions.length} ${retentionFilter === "all" ? "session" : retentionFilter} session${sessions.length !== 1 ? "s" : ""}`
      : `No ${retentionFilter === "all" ? "sessions" : `${retentionFilter} sessions`}`;

  // Group by "Session Status" (working / idle)
  const groupedBySessionStatus = useMemo(() => {
    const groups: Record<string, SessionListItem[]> = {
      working: [],
      idle: [],
    };
    for (const s of searchFiltered) {
      if (s.activityStatus === "busy") {
        groups.working.push(s);
      } else {
        groups.idle.push(s);
      }
    }
    return (
      <div className="space-y-4">
        {(["working", "idle"] as const).map((status) => {
          const items = sortSessions(groups[status]);
          if (items.length === 0) return null;
          return (
            <div key={status}>
              <div className="flex items-center gap-2 mb-2">
                <span className="text-xs font-semibold uppercase tracking-wider text-muted-foreground">
                  {status}
                </span>
                <span className="text-xs text-muted-foreground">({items.length})</span>
              </div>
               <div className="grid gap-3 sm:gap-4 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 card-grid-animated">
                 {nestSessions(items).map(({ item, children }) => (
                   <div key={`${item.instanceId}-${item.session.id}`} className="contents">
                     <LiveSessionCard
                       item={item}
                       isParent={children.length > 0}
                       onTerminate={handleTerminate}
                       onResume={handleResume}
                        onDelete={handleDeleteRequest}
                        onArchive={handleArchive}
                        onUnarchive={handleUnarchive}
                        onOpen={handleOpen}
                        onAbort={handleAbort}
                       isResuming={resumingSessionId === item.session.id}
                     />
                     {children.map((child) => (
                       <LiveSessionCard
                         key={`${child.instanceId}-${child.session.id}`}
                         item={child}
                         isChild
                         onTerminate={handleTerminate}
                         onResume={handleResume}
                          onDelete={handleDeleteRequest}
                          onArchive={handleArchive}
                          onUnarchive={handleUnarchive}
                          onOpen={handleOpen}
                          onAbort={handleAbort}
                         isResuming={resumingSessionId === child.session.id}
                       />
                     ))}
                   </div>
                 ))}
               </div>
             </div>
           );
         })}
       </div>
     );
  }, [searchFiltered, sortSessions, nestSessions, handleTerminate, handleResume, handleDeleteRequest, handleOpen, handleAbort, resumingSessionId]);

  // Group by "Connection Status" (connected / disconnected / stopped)
  const groupedByConnectionStatus = useMemo(() => {
    const groups: Record<string, SessionListItem[]> = {
      connected: [],
      disconnected: [],
      stopped: [],
    };
    for (const s of searchFiltered) {
      const isDisconnected = s.lifecycleStatus === "disconnected";
      const isStopped = s.lifecycleStatus === "stopped" || s.lifecycleStatus === "completed";

      if (isDisconnected) {
        groups.disconnected.push(s);
      } else if (isStopped) {
        groups.stopped.push(s);
      } else {
        groups.connected.push(s);
      }
    }
    return (
      <div className="space-y-4">
        {(["connected", "disconnected", "stopped"] as const).map((status) => {
          const items = sortSessions(groups[status]);
          if (items.length === 0) return null;
          return (
            <div key={status}>
              <div className="flex items-center gap-2 mb-2">
                <span className="text-xs font-semibold uppercase tracking-wider text-muted-foreground">
                  {status}
                </span>
                <span className="text-xs text-muted-foreground">({items.length})</span>
              </div>
               <div className="grid gap-3 sm:gap-4 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 card-grid-animated">
                 {nestSessions(items).map(({ item, children }) => (
                   <div key={`${item.instanceId}-${item.session.id}`} className="contents">
                     <LiveSessionCard
                       item={item}
                       isParent={children.length > 0}
                       onTerminate={handleTerminate}
                       onResume={handleResume}
                        onDelete={handleDeleteRequest}
                        onArchive={handleArchive}
                        onUnarchive={handleUnarchive}
                        onOpen={handleOpen}
                        onAbort={handleAbort}
                       isResuming={resumingSessionId === item.session.id}
                     />
                     {children.map((child) => (
                       <LiveSessionCard
                         key={`${child.instanceId}-${child.session.id}`}
                         item={child}
                         isChild
                         onTerminate={handleTerminate}
                         onResume={handleResume}
                          onDelete={handleDeleteRequest}
                          onArchive={handleArchive}
                          onUnarchive={handleUnarchive}
                          onOpen={handleOpen}
                          onAbort={handleAbort}
                         isResuming={resumingSessionId === child.session.id}
                       />
                     ))}
                   </div>
                 ))}
               </div>
             </div>
           );
         })}
       </div>
     );
  }, [searchFiltered, sortSessions, nestSessions, handleTerminate, handleResume, handleDeleteRequest, handleOpen, handleAbort, resumingSessionId]);

   // Group by "Source" (isolationStrategy)
   const groupedBySource = useMemo(() => {
    const sourceMap = new Map<string, SessionListItem[]>();
    for (const s of searchFiltered) {
      const key = s.isolationStrategy ?? "existing";
      const arr = sourceMap.get(key);
      if (arr) {
        arr.push(s);
      } else {
        sourceMap.set(key, [s]);
      }
    }
    return (
      <div className="space-y-4">
        {Array.from(sourceMap.entries()).map(([source, items]) => {
          const sorted = sortSessions(items);
          return (
            <div key={source}>
              <div className="flex items-center gap-2 mb-2">
                <span className="text-xs font-semibold uppercase tracking-wider text-muted-foreground">
                  {source}
                </span>
                <span className="text-xs text-muted-foreground">({sorted.length})</span>
              </div>
               <div className="grid gap-3 sm:gap-4 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 card-grid-animated">
                 {nestSessions(sorted).map(({ item, children }) => (
                   <div key={`${item.instanceId}-${item.session.id}`} className="contents">
                     <LiveSessionCard
                       item={item}
                       isParent={children.length > 0}
                       onTerminate={handleTerminate}
                       onResume={handleResume}
                        onDelete={handleDeleteRequest}
                        onArchive={handleArchive}
                        onUnarchive={handleUnarchive}
                        onOpen={handleOpen}
                        onAbort={handleAbort}
                       isResuming={resumingSessionId === item.session.id}
                     />
                     {children.map((child) => (
                       <LiveSessionCard
                         key={`${child.instanceId}-${child.session.id}`}
                         item={child}
                         isChild
                         onTerminate={handleTerminate}
                         onResume={handleResume}
                          onDelete={handleDeleteRequest}
                          onArchive={handleArchive}
                          onUnarchive={handleUnarchive}
                          onOpen={handleOpen}
                          onAbort={handleAbort}
                         isResuming={resumingSessionId === child.session.id}
                       />
                     ))}
                   </div>
                 ))}
               </div>
            </div>
          );
        })}
      </div>
    );
  }, [searchFiltered, sortSessions, nestSessions, handleTerminate, handleResume, handleDeleteRequest, handleOpen, handleAbort, resumingSessionId]);

  const renderContent = () => {
    if (searchFiltered.length === 0 && !isLoading) {
      if (search.trim()) {
        return (
          <div className="flex items-center justify-center h-32 text-muted-foreground text-sm">
            No sessions match your search.
          </div>
        );
      }
      if (workspaceFilter) {
        return (
          <div className="flex items-center justify-center h-32 text-muted-foreground text-sm">
            No sessions in this workspace.
          </div>
        );
      }
      return null;
    }

    if (prefs.groupBy === "none") {
      const nested = nestSessions(sortSessions(searchFiltered));
      return (
        <div className="grid gap-3 sm:gap-4 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 card-grid-animated">
          {nested.map(({ item, children }) => (
            <div key={`${item.instanceId}-${item.session.id}`} className="contents">
              <LiveSessionCard
                item={item}
                isParent={children.length > 0}
                onTerminate={handleTerminate}
                onResume={handleResume}
                onDelete={handleDeleteRequest}
                onArchive={handleArchive}
                onUnarchive={handleUnarchive}
                onOpen={handleOpen}
                onAbort={handleAbort}
                isResuming={resumingSessionId === item.session.id}
              />
              {children.map((child) => (
                <LiveSessionCard
                  key={`${child.instanceId}-${child.session.id}`}
                  item={child}
                  isChild
                  onTerminate={handleTerminate}
                  onResume={handleResume}
                  onDelete={handleDeleteRequest}
                  onArchive={handleArchive}
                  onUnarchive={handleUnarchive}
                  onOpen={handleOpen}
                  onAbort={handleAbort}
                  isResuming={resumingSessionId === child.session.id}
                />
              ))}
            </div>
          ))}
        </div>
      );
    }

    if (prefs.groupBy === "session-status") {
      return groupedBySessionStatus;
    }

    if (prefs.groupBy === "connection-status") {
      return groupedByConnectionStatus;
    }

    if (prefs.groupBy === "source") {
      return groupedBySource;
    }

    // Default: "directory" — render SessionGroup per workspace
    return (
      <div className="space-y-2">
        {deferredWorkspaceGroups.map((group) => (
          <SessionGroup
              key={group.workspaceDirectory}
              group={group}
              onTerminate={handleTerminate}
              onResume={handleResume}
              onDelete={handleDeleteRequest}
              onArchive={handleArchive}
              onUnarchive={handleUnarchive}
              onAbort={handleAbort}
              onOpen={handleOpen}
              resumingSessionId={resumingSessionId}
              refetch={refetch}
            />
        ))}
      </div>
    );
  };

  return (
    <div className="flex flex-col h-full">
      <Header
        title="Agent Fleet"
        subtitle={subtitle}
        actions={<NewSessionButton />}
      />
      <div className={`flex-1 overflow-auto p-3 sm:p-4 lg:p-6 space-y-4 sm:space-y-6${isFolded ? " fold-left" : ""}`}>
        <SummaryBar summary={summary} />

        <FleetToolbar
          groupBy={prefs.groupBy}
          sortBy={prefs.sortBy}
          retentionFilter={retentionFilter}
          search={search}
          onGroupByChange={handleGroupByChange}
          onSortByChange={handleSortByChange}
          onRetentionFilterChange={handleRetentionFilterChange}
          onSearchChange={setSearch}
        />

        {isLoading && sessions.length === 0 && (
          <div className="flex min-h-[40vh] items-center justify-center px-4 py-8">
            <div className="flex w-full max-w-xs flex-col items-center gap-3 rounded-2xl border border-border/60 bg-background/80 px-6 py-8 text-center shadow-sm backdrop-blur">
              <div className="flex h-12 w-12 items-center justify-center rounded-full bg-primary/10 text-primary">
                <Loader2 className="h-6 w-6 animate-spin" />
              </div>
              <div className="space-y-1">
                <p className="text-sm font-medium text-foreground">Loading sessions…</p>
                <p className="text-xs text-muted-foreground">Fetching the latest agent activity.</p>
              </div>
            </div>
          </div>
        )}

        {error && (
          <div className="rounded-md bg-red-500/10 border border-red-500/20 px-4 py-3 text-sm text-red-600 dark:text-red-400">
            Failed to load sessions: {error}
          </div>
        )}

        {renderContent()}

        {!isLoading && sessions.length === 0 && !error && (
          <div data-testid="empty-state" className="flex flex-col items-center justify-center h-48 text-muted-foreground text-sm gap-3">
            <p>No sessions running.</p>
            <p className="text-xs">Click &ldquo;New Session&rdquo; to start a new agent session.</p>
          </div>
        )}
      </div>

      <ConfirmDeleteSessionDialog
        open={!!deleteTarget}
        onOpenChange={(open) => { if (!open) setDeleteTarget(null); }}
        sessionTitle={deleteTarget?.title ?? ""}
        onConfirm={handleDeleteConfirm}
        isDeleting={isDeleting}
      />
    </div>
  );
}

export default function FleetPage() {
  return (
    <Suspense
      fallback={
        <div className="flex min-h-screen items-center justify-center px-6 py-10">
          <div className="flex w-full max-w-sm flex-col items-center gap-4 rounded-2xl border border-border/60 bg-background/80 px-8 py-10 text-center shadow-sm backdrop-blur">
            <div className="flex h-14 w-14 items-center justify-center rounded-full bg-primary/10 text-primary">
              <Loader2 className="h-7 w-7 animate-spin" />
            </div>
            <div className="space-y-1">
              <p className="text-sm font-medium text-foreground">Loading fleet…</p>
              <p className="text-xs text-muted-foreground">Preparing the latest session overview.</p>
            </div>
          </div>
        </div>
      }
    >
      <FleetPageInner />
    </Suspense>
  );
}
