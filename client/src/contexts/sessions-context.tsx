
import { createContext, useContext, useState, useEffect, useRef, useMemo, useCallback } from "react";
import { useSessions } from "@/hooks/use-sessions";
import { useFleetSummary } from "@/hooks/use-fleet-summary";
import { useActivityStream } from "@/hooks/use-activity-stream";
import type { SessionListItem } from "@/lib/api-types";
import type { FleetSummaryResponse } from "@/hooks/use-fleet-summary";
import type { SessionActivityStatus } from "@/lib/types";

export interface SessionsContextValue {
  sessions: SessionListItem[];
  isLoading: boolean;
  error?: string;
  refetch: () => void;
  summary: FleetSummaryResponse | null;
  /** Optimistically update a session's title before the next poll arrives. */
  patchSessionTitle: (sessionId: string, title: string) => void;
  /** Optimistically update a workspace's display name before the next poll arrives. */
  patchWorkspaceDisplayName: (workspaceId: string, displayName: string) => void;
}

const defaultValue: SessionsContextValue = {
  sessions: [],
  isLoading: true,
  error: undefined,
  refetch: () => {},
  summary: null,
  patchSessionTitle: () => {},
  patchWorkspaceDisplayName: () => {},
};

const SessionsContext = createContext<SessionsContextValue>(defaultValue);

/**
 * Map an incoming activityStatus to the legacy sessionStatus field.
 * Mirrors the server-side deriveActivityStatus mapping (in reverse).
 */
export function activityToSessionStatus(
  activityStatus: SessionActivityStatus
): SessionListItem["sessionStatus"] {
  switch (activityStatus) {
    case "busy":
      return "active";
    case "idle":
      return "idle";
    case "waiting_input":
      return "waiting_input";
  }
}

/**
 * Patch a single session's activity status in the sessions array.
 * Returns a new array only if a matching session was found and changed.
 */
export function patchActivityStatus(
  sessions: SessionListItem[],
  sessionId: string,
  activityStatus: SessionActivityStatus
): SessionListItem[] {
  const index = sessions.findIndex((s) => s.session.id === sessionId);
  if (index === -1) return sessions;

  const existing = sessions[index];
  // Skip patch if already the same
  if (existing.activityStatus === activityStatus) return sessions;

  const updated = sessions.slice();
  updated[index] = {
    ...existing,
    activityStatus,
    sessionStatus: activityToSessionStatus(activityStatus),
    lifecycleStatus: "running",
  };
  return updated;
}

/**
 * Prune SSE patches that the poll has caught up with.
 * Keeps patches where the polled status differs (SSE is still ahead of the poll).
 * Drops patches where the poll already matches or the session is no longer in the poll results.
 */
export function pruneStalePatches(
  patches: Map<string, SessionActivityStatus>,
  polledSessions: SessionListItem[]
): Map<string, SessionActivityStatus> {
  if (patches.size === 0) return patches;

  const remaining = new Map<string, SessionActivityStatus>();
  for (const [sessionId, patchedStatus] of patches) {
    const polled = polledSessions.find((s) => s.session.id === sessionId);
    // Keep patch only if polled status doesn't match (SSE is still ahead)
    if (polled && polled.activityStatus !== patchedStatus) {
      remaining.set(sessionId, patchedStatus);
    }
    // If session not in poll results, drop the patch (session may be deleted)
  }
  return remaining;
}

/**
 * Patch a single session's token/cost data in the sessions array.
 * Returns a new array only if a matching session was found and changed.
 */
export function patchTokenData(
  sessions: SessionListItem[],
  sessionId: string,
  totalTokens: number,
  totalCost: number
): SessionListItem[] {
  const index = sessions.findIndex((s) => s.session.id === sessionId);
  if (index === -1) return sessions;

  const existing = sessions[index];
  // Skip patch if already the same
  if (existing.totalTokens === totalTokens && existing.totalCost === totalCost) return sessions;

  const updated = sessions.slice();
  updated[index] = {
    ...existing,
    totalTokens,
    totalCost,
  };
  return updated;
}

export function SessionsProvider({ children }: { children: React.ReactNode }) {
  const { sessions: polledSessions, isLoading, error, refetch } = useSessions(15000);
  const { summary } = useFleetSummary(30000);

  // SSE patches stored in a ref to avoid setState-in-effect lint violations.
  // The ref is mutated by the SSE onmessage handler and read by useMemo.
  // When polledSessions changes (new poll arrived), we clear patches because
  // the poll is the source of truth.
  const ssePatchesRef = useRef<Map<string, SessionActivityStatus>>(new Map());
  const tokenPatchesRef = useRef<Map<string, { totalTokens: number; totalCost: number }>>(new Map());
  const lastPolledRef = useRef(polledSessions);
  const [sseGeneration, setSseGeneration] = useState(0);
  const rafRef = useRef<number | null>(null);

  // Optimistic rename patches — cleared per-entry when the poll catches up.
  const titlePatchesRef = useRef<Map<string, string>>(new Map());
  const displayNamePatchesRef = useRef<Map<string, string>>(new Map());
  const [renameGeneration, setRenameGeneration] = useState(0);

  // Subscribe to the shared SSE singleton for activity_status events
  const sse = useActivityStream();

  useEffect(() => {
    function handleActivityStatus(payload: unknown) {
      const msg = payload as {
        type: string;
        payload?: {
          sessionId: string;
          activityStatus: SessionActivityStatus;
        };
      };
      if (!msg.payload) return;
      ssePatchesRef.current = new Map(ssePatchesRef.current);
      ssePatchesRef.current.set(msg.payload.sessionId, msg.payload.activityStatus);
      // rAF batching (from Plan 1 Task 5)
      if (rafRef.current === null) {
        rafRef.current = requestAnimationFrame(() => {
          rafRef.current = null;
          setSseGeneration((n) => n + 1);
        });
      }
    }

    function handleTokenUpdate(payload: unknown) {
      const msg = payload as {
        type: string;
        payload?: {
          sessionId: string;
          totalTokens: number;
          totalCost: number;
        };
      };
      if (!msg.payload) return;
      tokenPatchesRef.current = new Map(tokenPatchesRef.current);
      tokenPatchesRef.current.set(msg.payload.sessionId, {
        totalTokens: msg.payload.totalTokens,
        totalCost: msg.payload.totalCost,
      });
      // rAF batching
      if (rafRef.current === null) {
        rafRef.current = requestAnimationFrame(() => {
          rafRef.current = null;
          setSseGeneration((n) => n + 1);
        });
      }
    }

    sse.on("activity_status", handleActivityStatus);
    sse.on("token_update", handleTokenUpdate);
    return () => {
      sse.off("activity_status", handleActivityStatus);
      sse.off("token_update", handleTokenUpdate);
      if (rafRef.current !== null) {
        cancelAnimationFrame(rafRef.current);
        rafRef.current = null;
      }
    };
  }, [sse]);

  // Merge polled sessions with any pending SSE patches.
  // Prune patches that the poll has caught up with (instead of clearing all).
  const sessions = useMemo(() => {
    if (lastPolledRef.current !== polledSessions) {
      lastPolledRef.current = polledSessions;
      // Smart pruning: only drop patches where the poll has caught up
      ssePatchesRef.current = pruneStalePatches(ssePatchesRef.current, polledSessions);

      // Prune token patches whose values now match what the poll returned
      for (const [sessionId, patchedTokens] of tokenPatchesRef.current) {
        const polled = polledSessions.find((s) => s.session.id === sessionId);
        if (!polled || (polled.totalTokens === patchedTokens.totalTokens && polled.totalCost === patchedTokens.totalCost)) {
          tokenPatchesRef.current.delete(sessionId);
        }
      }

      // Prune title patches whose value now matches what the poll returned
      for (const [sessionId, patchedTitle] of titlePatchesRef.current) {
        const polled = polledSessions.find((s) => s.session.id === sessionId);
        if (!polled || polled.session.title === patchedTitle) {
          titlePatchesRef.current.delete(sessionId);
        }
      }

      // Prune displayName patches whose value now matches what the poll returned
      for (const [workspaceId, patchedName] of displayNamePatchesRef.current) {
        const polled = polledSessions.find((s) => s.workspaceId === workspaceId);
        if (!polled || polled.workspaceDisplayName === patchedName) {
          displayNamePatchesRef.current.delete(workspaceId);
        }
      }
    }

    const patches = ssePatchesRef.current;
    const tokenPatches = tokenPatchesRef.current;
    const titlePatches = titlePatchesRef.current;
    const displayNamePatches = displayNamePatchesRef.current;

    let result = polledSessions;

    if (patches.size > 0) {
      for (const [sessionId, activityStatus] of patches) {
        result = patchActivityStatus(result, sessionId, activityStatus);
      }
    }

    if (tokenPatches.size > 0) {
      for (const [sessionId, { totalTokens, totalCost }] of tokenPatches) {
        result = patchTokenData(result, sessionId, totalTokens, totalCost);
      }
    }

    if (titlePatches.size > 0 || displayNamePatches.size > 0) {
      result = result.map((item) => {
        const newTitle = titlePatches.get(item.session.id);
        const newDisplayName = displayNamePatches.get(item.workspaceId);
        if (newTitle === undefined && newDisplayName === undefined) return item;
        return {
          ...item,
          ...(newTitle !== undefined ? { session: { ...item.session, title: newTitle } } : {}),
          ...(newDisplayName !== undefined ? { workspaceDisplayName: newDisplayName } : {}),
        };
      });
    }

    return result;
    // sseGeneration + renameGeneration counters trigger re-evaluation when patches arrive
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [polledSessions, sseGeneration, renameGeneration]);

  const patchSessionTitle = useCallback((sessionId: string, title: string) => {
    titlePatchesRef.current = new Map(titlePatchesRef.current);
    titlePatchesRef.current.set(sessionId, title);
    setRenameGeneration((n) => n + 1);
  }, []);

  const patchWorkspaceDisplayName = useCallback((workspaceId: string, displayName: string) => {
    displayNamePatchesRef.current = new Map(displayNamePatchesRef.current);
    displayNamePatchesRef.current.set(workspaceId, displayName);
    setRenameGeneration((n) => n + 1);
  }, []);

  const contextValue = useMemo(
    () => ({ sessions, isLoading, error, refetch, summary, patchSessionTitle, patchWorkspaceDisplayName }),
    [sessions, isLoading, error, refetch, summary, patchSessionTitle, patchWorkspaceDisplayName]
  );

  return (
    <SessionsContext.Provider value={contextValue}>
      {children}
    </SessionsContext.Provider>
  );
}

export function useSessionsContext(): SessionsContextValue {
  return useContext(SessionsContext);
}
