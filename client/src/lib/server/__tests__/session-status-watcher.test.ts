/**
 * Tests for session-status-watcher.ts — state management and event processing.
 *
 * Tests the public API: ensureWatching / stopWatching / _resetForTests.
 * Event processing tests push events directly via the captured hub listener
 * (no mock async iterable needed — the hub owns the subscription).
 */

import { vi, describe, it, expect, beforeEach, afterEach } from "vitest";

// Mock process-manager to prevent real instance lookups and recovery
vi.mock("@/lib/server/process-manager", () => ({
  getInstance: vi.fn(() => undefined),
  _recoveryComplete: Promise.resolve(),
}));

// Mock instance-event-hub — capture the registered listener for direct event injection
vi.mock("@/lib/server/instance-event-hub", () => {
  const listeners = new Map<string, (event: { type: string; properties: Record<string, unknown> }) => void>();
  return {
    addListener: vi.fn((instanceId: string, listener: (event: { type: string; properties: Record<string, unknown> }) => void) => {
      listeners.set(instanceId, listener);
      return () => {
        listeners.delete(instanceId);
      };
    }),
    removeAllListeners: vi.fn(),
    _resetForTests: vi.fn(() => {
      listeners.clear();
    }),
    // Expose for tests to push events
    __listeners: listeners,
  };
});

// Mock db-repository
vi.mock("@/lib/server/db-repository", () => ({
  getSessionByHarnessId: vi.fn(() => undefined),
  updateSessionStatus: vi.fn(),
  getSession: vi.fn(() => undefined),
  getActiveChildSessions: vi.fn(() => []),
}));

// Mock activity-emitter
vi.mock("@/lib/server/activity-emitter", () => ({
  emitActivityStatus: vi.fn(),
}));

// Mock analytics-collector — message.part.updated events delegate here
vi.mock("@/lib/server/analytics-collector", () => ({
  recordTokens: vi.fn(),
}));

import { ensureWatching, stopWatching, _resetForTests } from "@/lib/server/session-status-watcher";
import * as processManager from "@/lib/server/process-manager";
import * as instanceEventHub from "@/lib/server/instance-event-hub";
import * as dbRepository from "@/lib/server/db-repository";
import * as activityEmitter from "@/lib/server/activity-emitter";
import * as analyticsCollector from "@/lib/server/analytics-collector";

// Helper to push an event to the captured listener for a given instance
// eslint-disable-next-line @typescript-eslint/no-explicit-any
function pushEvent(instanceId: string, event: { type: string; properties: Record<string, any> }): void {
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  const listeners = (instanceEventHub as any).__listeners as Map<string, (e: typeof event) => void>;
  const listener = listeners.get(instanceId);
  if (!listener) throw new Error(`No listener registered for instance ${instanceId}`);
  listener(event);
}

// ─── Setup ────────────────────────────────────────────────────────────────────

beforeEach(() => {
  _resetForTests();
  vi.clearAllMocks();
});

afterEach(() => {
  _resetForTests();
});

// ─── Tests ────────────────────────────────────────────────────────────────────

describe("session-status-watcher", () => {
  describe("ensureWatching / stopWatching", () => {
    it("EnsureWatchingDoesNotThrowWhenInstanceIsDead", () => {
      vi.mocked(processManager.getInstance).mockReturnValue(undefined);
      expect(() => ensureWatching("inst-1")).not.toThrow();
    });

    it("StopWatchingIsNoOpForNonExistentInstance", () => {
      expect(() => stopWatching("nonexistent")).not.toThrow();
    });

    it("DoubleEnsureWatchingIsIdempotent", () => {
      vi.mocked(processManager.getInstance).mockReturnValue({
        directory: "/test",
        status: "running",
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
      } as any);

      ensureWatching("inst-1");
      ensureWatching("inst-1");

      // addListener should only be called once
      expect(instanceEventHub.addListener).toHaveBeenCalledTimes(1);
    });

    it("StopWatchingAfterEnsureWatchingDoesNotThrow", () => {
      vi.mocked(processManager.getInstance).mockReturnValue({
        directory: "/test",
        status: "running",
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
      } as any);
      ensureWatching("inst-1");
      expect(() => stopWatching("inst-1")).not.toThrow();
    });

    it("DoubleStopIsNoOp", () => {
      vi.mocked(processManager.getInstance).mockReturnValue({
        directory: "/test",
        status: "running",
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
      } as any);
      ensureWatching("inst-1");
      stopWatching("inst-1");
      expect(() => stopWatching("inst-1")).not.toThrow();
    });
  });

  describe("_resetForTests", () => {
    it("ClearsAllStateWithoutError", () => {
      vi.mocked(processManager.getInstance).mockReturnValue({
        directory: "/test",
        status: "running",
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
      } as any);
      ensureWatching("inst-1");
      ensureWatching("inst-2");

      expect(() => _resetForTests()).not.toThrow();
    });

    it("AllowsReWatchingAfterReset", () => {
      vi.mocked(processManager.getInstance).mockReturnValue({
        directory: "/test",
        status: "running",
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
      } as any);
      ensureWatching("inst-1");
      _resetForTests();
      vi.clearAllMocks();

      // Should register a new listener after reset
      expect(() => ensureWatching("inst-1")).not.toThrow();
      expect(instanceEventHub.addListener).toHaveBeenCalledTimes(1);
    });
  });

  describe("event processing", () => {
    const instanceId = "inst-test";

    function setupWatching() {
      vi.mocked(processManager.getInstance).mockReturnValue({
        directory: "/test",
        status: "running",
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
      } as any);
      ensureWatching(instanceId);
    }

    it("EmitsActivityStatusOnBusyTransition", () => {
      setupWatching();

      vi.mocked(dbRepository.getSessionByHarnessId).mockReturnValue({
        id: "db-1",
        status: "idle",
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
      } as any);

      pushEvent(instanceId, {
        type: "session.status",
        properties: {
          sessionID: "oc-sess-1",
          status: { type: "busy" },
        },
      });

      expect(activityEmitter.emitActivityStatus).toHaveBeenCalledWith({
        sessionId: "oc-sess-1",
        instanceId,
        activityStatus: "busy",
      });
      expect(dbRepository.updateSessionStatus).toHaveBeenCalledWith("db-1", "active");
    });

    it("EmitsActivityStatusOnIdleTransition", () => {
      setupWatching();

      vi.mocked(dbRepository.getSessionByHarnessId).mockReturnValue({
        id: "db-2",
        status: "active",
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
      } as any);

      pushEvent(instanceId, {
        type: "session.status",
        properties: {
          sessionID: "oc-sess-2",
          status: { type: "idle" },
        },
      });

      expect(activityEmitter.emitActivityStatus).toHaveBeenCalledWith({
        sessionId: "oc-sess-2",
        instanceId,
        activityStatus: "idle",
      });
      expect(dbRepository.updateSessionStatus).toHaveBeenCalledWith("db-2", "idle");
    });

    it("EmitsActivityStatusOnSessionIdleEvent", () => {
      setupWatching();

      vi.mocked(dbRepository.getSessionByHarnessId).mockReturnValue({
        id: "db-3",
        status: "active",
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
      } as any);

      pushEvent(instanceId, {
        type: "session.idle",
        properties: {
          sessionID: "oc-sess-3",
        },
      });

      expect(activityEmitter.emitActivityStatus).toHaveBeenCalledWith({
        sessionId: "oc-sess-3",
        instanceId,
        activityStatus: "idle",
      });
      expect(dbRepository.updateSessionStatus).toHaveBeenCalledWith("db-3", "idle");
    });

    it("EmitsWaitingInputOnPermissionEvent", () => {
      setupWatching();

      vi.mocked(dbRepository.getSessionByHarnessId).mockReturnValue({
        id: "db-4",
        status: "active",
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
      } as any);

      pushEvent(instanceId, {
        type: "permission.request",
        properties: {
          sessionID: "oc-sess-4",
        },
      });

      expect(activityEmitter.emitActivityStatus).toHaveBeenCalledWith({
        sessionId: "oc-sess-4",
        instanceId,
        activityStatus: "waiting_input",
      });
      expect(dbRepository.updateSessionStatus).toHaveBeenCalledWith("db-4", "waiting_input");
    });

    it("DoesNotEmitWhenStatusUnchanged", () => {
      setupWatching();

      // DB already has status "idle" — same as what the event reports
      vi.mocked(dbRepository.getSessionByHarnessId).mockReturnValue({
        id: "db-5",
        status: "idle",
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
      } as any);

      pushEvent(instanceId, {
        type: "session.status",
        properties: {
          sessionID: "oc-sess-5",
          status: { type: "idle" },
        },
      });

      expect(activityEmitter.emitActivityStatus).not.toHaveBeenCalled();
      expect(dbRepository.updateSessionStatus).not.toHaveBeenCalled();
    });

    it("DoesNotEmitForUnknownSession", () => {
      setupWatching();

      vi.mocked(dbRepository.getSessionByHarnessId).mockReturnValue(undefined);

      pushEvent(instanceId, {
        type: "session.status",
        properties: {
          sessionID: "oc-unknown",
          status: { type: "busy" },
        },
      });

      expect(activityEmitter.emitActivityStatus).not.toHaveBeenCalled();
      expect(dbRepository.updateSessionStatus).not.toHaveBeenCalled();
    });

    it("MessagePartUpdatedDelegatesToRecordTokens", () => {
      setupWatching();

      pushEvent(instanceId, {
        type: "message.part.updated",
        properties: {
          part: {
            type: "step-finish",
            sessionID: "oc-sess-1",
            tokens: { input: 50, output: 30, reasoning: 10 },
            cost: 0.02,
          },
        },
      });

      expect(analyticsCollector.recordTokens).toHaveBeenCalledWith("oc-sess-1", 90, 0.02);
      // Must NOT do any DB work or emit inline
      expect(dbRepository.updateSessionStatus).not.toHaveBeenCalled();
      expect(activityEmitter.emitActivityStatus).not.toHaveBeenCalled();
    });

    it("MessagePartUpdatedIgnoresNonStepFinishParts", () => {
      setupWatching();

      pushEvent(instanceId, {
        type: "message.part.updated",
        properties: {
          part: {
            type: "text",
            sessionID: "oc-sess-1",
            tokens: { input: 10, output: 5 },
          },
        },
      });

      expect(analyticsCollector.recordTokens).not.toHaveBeenCalled();
    });

    it("MessagePartUpdatedIgnoresZeroDeltas", () => {
      setupWatching();

      pushEvent(instanceId, {
        type: "message.part.updated",
        properties: {
          part: {
            type: "step-finish",
            sessionID: "oc-sess-1",
            tokens: { input: 0, output: 0, reasoning: 0 },
            cost: 0,
          },
        },
      });

      expect(analyticsCollector.recordTokens).not.toHaveBeenCalled();
    });
  });

  describe("parent status propagation", () => {
    const instanceId = "inst-test";

    function setupWatching() {
      vi.mocked(processManager.getInstance).mockReturnValue({
        directory: "/test",
        status: "running",
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
      } as any);
      ensureWatching(instanceId);
    }

    it("PropagatesBusyToParentWhenChildBecomesBusy", () => {
      setupWatching();

      vi.mocked(dbRepository.getSessionByHarnessId).mockReturnValue({
        id: "child-db-1",
        status: "idle",
        parent_session_id: "parent-db-1",
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
      } as any);

      vi.mocked(dbRepository.getSession).mockReturnValue({
        id: "parent-db-1",
        status: "idle",
        opencode_session_id: "oc-parent-1",
        instance_id: "inst-parent",
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
      } as any);

      pushEvent(instanceId, {
        type: "session.status",
        properties: {
          sessionID: "oc-child-1",
          status: { type: "busy" },
        },
      });

      expect(activityEmitter.emitActivityStatus).toHaveBeenCalledTimes(2);

      expect(activityEmitter.emitActivityStatus).toHaveBeenCalledWith({
        sessionId: "oc-child-1",
        instanceId,
        activityStatus: "busy",
      });
      expect(activityEmitter.emitActivityStatus).toHaveBeenCalledWith({
        sessionId: "oc-parent-1",
        instanceId: "inst-parent",
        activityStatus: "busy",
      });
      expect(dbRepository.updateSessionStatus).toHaveBeenCalledWith("child-db-1", "active");
      expect(dbRepository.updateSessionStatus).toHaveBeenCalledWith("parent-db-1", "active");
    });

    it("PropagatesIdleToParentWhenLastChildGoesIdle", () => {
      setupWatching();

      vi.mocked(dbRepository.getSessionByHarnessId).mockReturnValue({
        id: "child-db-2",
        status: "active",
        parent_session_id: "parent-db-2",
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
      } as any);

      vi.mocked(dbRepository.getSession).mockReturnValue({
        id: "parent-db-2",
        status: "active",
        opencode_session_id: "oc-parent-2",
        instance_id: "inst-parent",
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
      } as any);

      vi.mocked(dbRepository.getActiveChildSessions).mockReturnValue([]);

      pushEvent(instanceId, {
        type: "session.status",
        properties: {
          sessionID: "oc-child-2",
          status: { type: "idle" },
        },
      });

      expect(activityEmitter.emitActivityStatus).toHaveBeenCalledTimes(2);

      expect(activityEmitter.emitActivityStatus).toHaveBeenCalledWith({
        sessionId: "oc-child-2",
        instanceId,
        activityStatus: "idle",
      });
      expect(activityEmitter.emitActivityStatus).toHaveBeenCalledWith({
        sessionId: "oc-parent-2",
        instanceId: "inst-parent",
        activityStatus: "idle",
      });
      expect(dbRepository.updateSessionStatus).toHaveBeenCalledWith("parent-db-2", "idle");
    });

    it("DoesNotPropagateIdleToParentWhenOtherChildrenStillActive", () => {
      setupWatching();

      vi.mocked(dbRepository.getSessionByHarnessId).mockReturnValue({
        id: "child-db-3",
        status: "active",
        parent_session_id: "parent-db-3",
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
      } as any);

      vi.mocked(dbRepository.getSession).mockReturnValue({
        id: "parent-db-3",
        status: "active",
        opencode_session_id: "oc-parent-3",
        instance_id: "inst-parent",
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
      } as any);

      vi.mocked(dbRepository.getActiveChildSessions).mockReturnValue([
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
        { id: "sibling-db-1", status: "active" } as any,
      ]);

      pushEvent(instanceId, {
        type: "session.status",
        properties: {
          sessionID: "oc-child-3",
          status: { type: "idle" },
        },
      });

      expect(activityEmitter.emitActivityStatus).toHaveBeenCalledTimes(1);
      expect(activityEmitter.emitActivityStatus).toHaveBeenCalledWith({
        sessionId: "oc-child-3",
        instanceId,
        activityStatus: "idle",
      });
    });

    it("DoesNotPropagateToTerminalParent", () => {
      setupWatching();

      vi.mocked(dbRepository.getSessionByHarnessId).mockReturnValue({
        id: "child-db-4",
        status: "idle",
        parent_session_id: "parent-db-4",
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
      } as any);

      vi.mocked(dbRepository.getSession).mockReturnValue({
        id: "parent-db-4",
        status: "stopped",
        opencode_session_id: "oc-parent-4",
        instance_id: "inst-parent",
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
      } as any);

      pushEvent(instanceId, {
        type: "session.status",
        properties: {
          sessionID: "oc-child-4",
          status: { type: "busy" },
        },
      });

      expect(activityEmitter.emitActivityStatus).toHaveBeenCalledTimes(1);
      expect(activityEmitter.emitActivityStatus).toHaveBeenCalledWith({
        sessionId: "oc-child-4",
        instanceId,
        activityStatus: "busy",
      });
      expect(dbRepository.updateSessionStatus).toHaveBeenCalledTimes(1);
      expect(dbRepository.updateSessionStatus).toHaveBeenCalledWith("child-db-4", "active");
    });

    it("DoesNotPropagateWhenChildHasNoParent", () => {
      setupWatching();

      vi.mocked(dbRepository.getSessionByHarnessId).mockReturnValue({
        id: "child-db-5",
        status: "idle",
        parent_session_id: null,
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
      } as any);

      pushEvent(instanceId, {
        type: "session.status",
        properties: {
          sessionID: "oc-child-5",
          status: { type: "busy" },
        },
      });

      expect(activityEmitter.emitActivityStatus).toHaveBeenCalledTimes(1);
      expect(dbRepository.getSession).not.toHaveBeenCalled();
    });
  });
});
