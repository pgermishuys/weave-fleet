// @vitest-environment jsdom

import React from "react";
import { act, renderHook, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { SessionListItem } from "@/lib/api-types";
import type { SessionActivityStatus } from "@/lib/types";
import {
  SessionsProvider,
  useSessionsContext,
  patchActivityStatus,
  activityToSessionStatus,
  pruneStalePatches,
} from "@/contexts/sessions-context";

const mockUseSessions = vi.fn();
const mockUseFleetSummary = vi.fn();
const mockUseActivityStream = vi.fn();

vi.mock("@/hooks/use-sessions", () => ({
  useSessions: (...args: unknown[]) => mockUseSessions(...args),
}));

vi.mock("@/hooks/use-fleet-summary", () => ({
  useFleetSummary: (...args: unknown[]) => mockUseFleetSummary(...args),
}));

vi.mock("@/hooks/use-activity-stream", () => ({
  useActivityStream: (...args: unknown[]) => mockUseActivityStream(...args),
}));

function makeSession(id: string, overrides: Partial<SessionListItem> = {}): SessionListItem {
  return {
    instanceId: "inst-1",
    workspaceId: "ws-1",
    workspaceDirectory: "/tmp/proj",
    workspaceDisplayName: null,
    isolationStrategy: "existing",
    sourceDirectory: null,
    branch: null,
    sessionStatus: "idle",
    instanceStatus: "running",
    session: { id, title: id, time: { created: 1, updated: 1 } },
    activityStatus: "idle",
    lifecycleStatus: "running",
    retentionStatus: "active",
    archivedAt: null,
    typedInstanceStatus: "running",
    isHidden: false,
    ...overrides,
  };
}

function wrapper({ children }: { children: React.ReactNode }) {
  return <SessionsProvider>{children}</SessionsProvider>;
}

describe("activityToSessionStatus", () => {
  it("MapsBusyToActive", () => {
    expect(activityToSessionStatus("busy")).toBe("active");
  });

  it("MapsIdleToIdle", () => {
    expect(activityToSessionStatus("idle")).toBe("idle");
  });

  it("MapsWaitingInputToWaitingInput", () => {
    expect(activityToSessionStatus("waiting_input")).toBe("waiting_input");
  });
});

describe("patchActivityStatus", () => {
  it("PatchesMatchingSession", () => {
    const sessions = [makeSession("sess-1", { activityStatus: "idle", sessionStatus: "idle" })];
    const result = patchActivityStatus(sessions, "sess-1", "busy");

    expect(result).not.toBe(sessions);
    expect(result[0]!.activityStatus).toBe("busy");
    expect(result[0]!.sessionStatus).toBe("active");
  });

  it("ReturnsSameArrayWhenSessionNotFound", () => {
    const sessions = [makeSession("sess-1"), makeSession("sess-2")];
    const result = patchActivityStatus(sessions, "sess-nonexistent", "busy");

    expect(result).toBe(sessions);
  });

  it("ReturnsSameArrayWhenStatusUnchanged", () => {
    const sessions = [makeSession("sess-1", { activityStatus: "busy" })];
    const result = patchActivityStatus(sessions, "sess-1", "busy");

    expect(result).toBe(sessions);
  });
});

describe("pruneStalePatches", () => {
  it("ReturnsSameMapWhenPatchesAreEmpty", () => {
    const patches = new Map<string, SessionActivityStatus>();
    const sessions = [makeSession("sess-1")];
    const result = pruneStalePatches(patches, sessions);

    expect(result).toBe(patches);
  });

  it("KeepsPatchWhenPolledStatusDiffersFromPatch", () => {
    const patches = new Map<string, SessionActivityStatus>([["sess-1", "busy"]]);
    const sessions = [makeSession("sess-1", { activityStatus: "idle" })];
    const result = pruneStalePatches(patches, sessions);

    expect(result.size).toBe(1);
    expect(result.get("sess-1")).toBe("busy");
  });

  it("PrunesPatchWhenPolledStatusMatchesPatch", () => {
    const patches = new Map<string, SessionActivityStatus>([["sess-1", "busy"]]);
    const sessions = [makeSession("sess-1", { activityStatus: "busy" })];
    const result = pruneStalePatches(patches, sessions);

    expect(result.size).toBe(0);
  });
});

describe("SessionsProvider", () => {
  let streamEmit: (eventType: string, payload: unknown) => void;

  beforeEach(() => {
    vi.clearAllMocks();

    mockUseSessions.mockReturnValue({
      sessions: [makeSession("sess-1")],
      isLoading: false,
      error: undefined,
      refetch: vi.fn(),
    });

    mockUseFleetSummary.mockReturnValue({
      summary: null,
      isLoading: false,
      error: undefined,
    });

    const listeners = new Map<string, Set<(payload: unknown) => void>>();
    mockUseActivityStream.mockReturnValue({
      on: (eventType: string, callback: (payload: unknown) => void) => {
        const set = listeners.get(eventType) ?? new Set();
        set.add(callback);
        listeners.set(eventType, set);
      },
      off: (eventType: string, callback: (payload: unknown) => void) => {
        listeners.get(eventType)?.delete(callback);
      },
      emit: (eventType: string, payload: unknown) => {
        listeners.get(eventType)?.forEach((callback) => callback(payload));
      },
    });
    streamEmit = (eventType, payload) => {
      const stream = mockUseActivityStream.mock.results[0]!.value as {
        emit: (eventType: string, payload: unknown) => void;
      };
      stream.emit(eventType, payload);
    };
  });

  it("includes retention filter defaults", () => {
    const { result } = renderHook(() => useSessionsContext(), { wrapper });
    expect(result.current.retentionFilter).toBe("active");
  });

  it("refetches when archiveDeleteAndStopLifecycleEventsArrive", async () => {
    const refetch = vi.fn();
    mockUseSessions.mockReturnValue({
      sessions: [makeSession("sess-1")],
      isLoading: false,
      error: undefined,
      refetch,
    });

    const { result } = renderHook(() => useSessionsContext(), { wrapper });
    const stream = mockUseActivityStream.mock.results[0]!.value as {
      emit: (eventType: string, payload: unknown) => void;
    };

    await act(async () => {
      stream.emit("session_created", { type: "session_created" });
      stream.emit("session_stopped", { type: "session_stopped" });
      stream.emit("session_archived", { type: "session_archived" });
      stream.emit("session_unarchived", { type: "session_unarchived" });
      stream.emit("session_deleted", { type: "session_deleted" });
    });

    await waitFor(() => {
      expect(refetch).toHaveBeenCalledTimes(5);
    });

    expect(result.current.sessions).toHaveLength(1);
  });

  it("patchesActivityStatusFromPropertiesFormat", async () => {
    mockUseSessions.mockReturnValue({
      sessions: [makeSession("sess-1", { activityStatus: "idle" })],
      isLoading: false,
      error: undefined,
      refetch: vi.fn(),
    });

    const { result } = renderHook(() => useSessionsContext(), { wrapper });

    await act(async () => {
      streamEmit("activity_status", {
        type: "activity_status",
        properties: { sessionId: "sess-1", activityStatus: "busy" },
      });
    });

    await waitFor(() => {
      expect(result.current.sessions[0]!.activityStatus).toBe("busy");
    });
  });

  it("ignoresActivityStatusEventWithoutProperties", async () => {
    mockUseSessions.mockReturnValue({
      sessions: [makeSession("sess-1", { activityStatus: "idle" })],
      isLoading: false,
      error: undefined,
      refetch: vi.fn(),
    });

    const { result } = renderHook(() => useSessionsContext(), { wrapper });

    await act(async () => {
      // Old format with "payload" field — should be ignored
      streamEmit("activity_status", {
        type: "activity_status",
        payload: { sessionId: "sess-1", activityStatus: "busy" },
      });
    });

    // Status should remain idle since "payload" field is no longer read
    expect(result.current.sessions[0]!.activityStatus).toBe("idle");
  });

  it("patchesMultipleSessionsFromInitialStateEvents", async () => {
    mockUseSessions.mockReturnValue({
      sessions: [
        makeSession("sess-1", { activityStatus: "idle" }),
        makeSession("sess-2", { activityStatus: "idle" }),
      ],
      isLoading: false,
      error: undefined,
      refetch: vi.fn(),
    });

    const { result } = renderHook(() => useSessionsContext(), { wrapper });

    await act(async () => {
      // Simulate initial state snapshot events sent on WebSocket subscribe
      streamEmit("activity_status", {
        type: "activity_status",
        properties: { sessionId: "sess-1", activityStatus: "busy" },
      });
      streamEmit("activity_status", {
        type: "activity_status",
        properties: { sessionId: "sess-2", activityStatus: "busy" },
      });
    });

    await waitFor(() => {
      expect(result.current.sessions[0]!.activityStatus).toBe("busy");
      expect(result.current.sessions[1]!.activityStatus).toBe("busy");
    });
  });
});
