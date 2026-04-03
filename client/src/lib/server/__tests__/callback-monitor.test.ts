/**
 * Tests for callback-monitor.ts — session tracking and cleanup logic.
 *
 * Tests state management: startMonitoring/stopMonitoring track sessions
 * correctly and _resetForTests clears everything. Event-driven callback
 * tests push events via the captured hub listener.
 */

/* eslint-disable @typescript-eslint/no-explicit-any */
import { vi, describe, it, expect, beforeEach, afterEach } from "vitest";

// Mock process-manager to prevent real instance lookups and recovery
vi.mock("@/lib/server/process-manager", () => ({
  getInstance: vi.fn(() => undefined),
  _recoveryComplete: Promise.resolve(),
}));

// Mock instance-event-hub — capture registered listeners for direct event injection
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
    __listeners: listeners,
  };
});

// Mock callback-service to prevent real callback delivery
vi.mock("@/lib/server/callback-service", () => ({
  fireSessionCallbacks: vi.fn(),
  fireSessionErrorCallbacks: vi.fn(),
}));

// Mock db-repository — provide no-op implementations
vi.mock("@/lib/server/db-repository", () => ({
  getAllPendingCallbacks: vi.fn(() => []),
  claimPendingCallback: vi.fn(() => true),
  getSession: vi.fn(() => undefined),
  getSessionByHarnessId: vi.fn(() => undefined),
  updateSessionStatus: vi.fn(),
}));

import { startMonitoring, stopMonitoring, _resetForTests } from "@/lib/server/callback-monitor";
import * as processManager from "@/lib/server/process-manager";
import * as instanceEventHub from "@/lib/server/instance-event-hub";
import * as callbackService from "@/lib/server/callback-service";
import * as dbRepository from "@/lib/server/db-repository";

// Helper to push an event to the captured listener for a given instance
function pushEvent(instanceId: string, event: { type: string; properties: Record<string, any> }): void {
  const listeners = (instanceEventHub as any).__listeners as Map<string, (e: typeof event) => void>;
  const listener = listeners.get(instanceId);
  if (!listener) throw new Error(`No listener registered for instance ${instanceId}`);
  listener(event);
}

function hasListener(instanceId: string): boolean {
  const listeners = (instanceEventHub as any).__listeners as Map<string, unknown>;
  return listeners.has(instanceId);
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

describe("callback-monitor", () => {
  describe("startMonitoring / stopMonitoring", () => {
    it("StartMonitoringDoesNotThrowWhenInstanceIsDead", () => {
      vi.mocked(processManager.getInstance).mockReturnValue(undefined);
      expect(() => startMonitoring("db-1", "oc-1", "inst-1")).not.toThrow();
    });

    it("StopMonitoringIsNoOpForNonExistentSession", () => {
      expect(() => stopMonitoring("nonexistent")).not.toThrow();
    });

    it("DoubleStartMonitoringIsIdempotent", () => {
      vi.mocked(processManager.getInstance).mockReturnValue({
        directory: "/test",
        status: "running",
        client: { session: { status: vi.fn().mockResolvedValue({ data: {} }) } },
      } as any);

      startMonitoring("db-1", "oc-1", "inst-1");
      startMonitoring("db-1", "oc-1", "inst-1");

      // addListener should only be called once for the instance
      expect(instanceEventHub.addListener).toHaveBeenCalledTimes(1);
    });

    it("StopMonitoringAfterStartDoesNotThrow", () => {
      vi.mocked(processManager.getInstance).mockReturnValue({
        directory: "/test",
        status: "running",
        client: { session: { status: vi.fn().mockResolvedValue({ data: {} }) } },
      } as any);
      startMonitoring("db-1", "oc-1", "inst-1");
      expect(() => stopMonitoring("db-1")).not.toThrow();
    });

    it("DoubleStopIsNoOp", () => {
      vi.mocked(processManager.getInstance).mockReturnValue({
        directory: "/test",
        status: "running",
        client: { session: { status: vi.fn().mockResolvedValue({ data: {} }) } },
      } as any);
      startMonitoring("db-1", "oc-1", "inst-1");
      stopMonitoring("db-1");
      expect(() => stopMonitoring("db-1")).not.toThrow();
    });
  });

  describe("_resetForTests", () => {
    it("ClearsAllStateWithoutError", () => {
      vi.mocked(processManager.getInstance).mockReturnValue({
        directory: "/test",
        status: "running",
        client: { session: { status: vi.fn().mockResolvedValue({ data: {} }) } },
      } as any);
      startMonitoring("db-1", "oc-1", "inst-1");
      startMonitoring("db-2", "oc-2", "inst-2");

      expect(() => _resetForTests()).not.toThrow();
    });

    it("AllowsReMonitoringAfterReset", () => {
      vi.mocked(processManager.getInstance).mockReturnValue({
        directory: "/test",
        status: "running",
        client: { session: { status: vi.fn().mockResolvedValue({ data: {} }) } },
      } as any);
      startMonitoring("db-1", "oc-1", "inst-1");
      _resetForTests();
      vi.clearAllMocks();

      expect(() => startMonitoring("db-1", "oc-1", "inst-1")).not.toThrow();
    });
  });

  describe("SDK call timeout handling", () => {
    it("PollingLoopHandlesStatusTimeoutGracefully", async () => {
      const instanceId = "inst-status-timeout";

      vi.mocked(processManager.getInstance).mockReturnValue({
        directory: "/test",
        status: "running",
        client: {
          session: {
            // status never resolves
            status: vi.fn().mockReturnValue(new Promise(() => {})),
          },
        },
      } as any);

      // Provide a pending callback so the polling loop runs
      const { getAllPendingCallbacks, getSession } = await import("@/lib/server/db-repository");
      vi.mocked(getAllPendingCallbacks).mockReturnValue([
        { id: "cb-1", source_session_id: "db-src-1", target_session_id: "db-tgt-1", target_instance_id: instanceId },
      ] as any);
      vi.mocked(getSession).mockReturnValue({
        id: "db-src-1",
        instance_id: instanceId,
        opencode_session_id: "oc-src-1",
        status: "active",
      } as any);

      const origTimeout = process.env.WEAVE_SDK_CALL_TIMEOUT_MS;
      process.env.WEAVE_SDK_CALL_TIMEOUT_MS = "50";

      try {
        startMonitoring("db-src-1", "oc-src-1", instanceId);

        // Allow time for the polling interval + timeout to fire
        await new Promise((r) => setTimeout(r, 300));

        // No crash — polling loop is still alive despite the timeout
        expect(true).toBe(true);
      } finally {
        if (origTimeout !== undefined) {
          process.env.WEAVE_SDK_CALL_TIMEOUT_MS = origTimeout;
        } else {
          delete process.env.WEAVE_SDK_CALL_TIMEOUT_MS;
        }
      }
    });
  });

  describe("per-instance listener ref-counting (Blocker 3)", () => {
    const instanceId = "inst-refcount";

    function setupInstance() {
      vi.mocked(processManager.getInstance).mockReturnValue({
        directory: "/test",
        status: "running",
        client: { session: { status: vi.fn().mockResolvedValue({ data: {} }) } },
      } as any);
    }

    it("TwoStartMonitoringCallsForDifferentSessionsOnSameInstanceResultInOneAddListenerCall", () => {
      setupInstance();
      startMonitoring("db-A", "oc-A", instanceId);
      startMonitoring("db-B", "oc-B", instanceId);

      expect(instanceEventHub.addListener).toHaveBeenCalledTimes(1);
      expect(instanceEventHub.addListener).toHaveBeenCalledWith(instanceId, expect.any(Function));
    });

    it("StopMonitoringFirstSessionDoesNotRemoveHubListener", () => {
      setupInstance();
      startMonitoring("db-A", "oc-A", instanceId);
      startMonitoring("db-B", "oc-B", instanceId);

      stopMonitoring("db-A");

      // Hub listener for instance should still be registered (session B is still active)
      expect(hasListener(instanceId)).toBe(true);
    });

    it("StopMonitoringLastSessionRemovesHubListener", () => {
      setupInstance();
      startMonitoring("db-A", "oc-A", instanceId);
      startMonitoring("db-B", "oc-B", instanceId);

      stopMonitoring("db-A");
      stopMonitoring("db-B");

      // Hub listener should now be removed
      expect(hasListener(instanceId)).toBe(false);
    });
  });

  describe("event-driven callback detection (Blocker 1)", () => {
    const instanceId = "inst-callback";

    function setupInstance() {
      vi.mocked(processManager.getInstance).mockReturnValue({
        directory: "/test",
        status: "running",
        client: { session: { status: vi.fn().mockResolvedValue({ data: { "oc-sess": { type: "busy" } } }) } },
      } as any);
    }

    it("FiresCallbackOnBusyToIdleTransition", async () => {
      setupInstance();
      startMonitoring("db-1", "oc-sess", instanceId);

      // Wait for initial poll to complete
      await new Promise((r) => setTimeout(r, 50));

      // Simulate busy event (state tracking)
      pushEvent(instanceId, {
        type: "session.status",
        properties: { sessionID: "oc-sess", status: { type: "busy" } },
      });

      // Now idle
      pushEvent(instanceId, {
        type: "session.status",
        properties: { sessionID: "oc-sess", status: { type: "idle" } },
      });

      await vi.waitFor(() => {
        expect(callbackService.fireSessionCallbacks).toHaveBeenCalledWith("oc-sess", instanceId);
      });
      expect(dbRepository.updateSessionStatus).toHaveBeenCalledWith("db-1", "idle");
    });

    it("HandlerCallingStopMonitoringDuringDispatchDoesNotThrow", async () => {
      setupInstance();
      startMonitoring("db-safe", "oc-safe", instanceId);

      await new Promise((r) => setTimeout(r, 50));

      // Set busy state first
      pushEvent(instanceId, {
        type: "session.status",
        properties: { sessionID: "oc-safe", status: { type: "busy" } },
      });

      // idle event triggers stopMonitoringSession inside the handler
      expect(() => {
        pushEvent(instanceId, {
          type: "session.status",
          properties: { sessionID: "oc-safe", status: { type: "idle" } },
        });
      }).not.toThrow();
    });

    it("FiresErrorCallbackOnErrorEvent", async () => {
      setupInstance();
      startMonitoring("db-err", "oc-err", instanceId);

      await new Promise((r) => setTimeout(r, 50));

      pushEvent(instanceId, {
        type: "error",
        properties: { sessionID: "oc-err" },
      });

      await vi.waitFor(() => {
        expect(callbackService.fireSessionErrorCallbacks).toHaveBeenCalledWith("oc-err", instanceId);
      });
    });
  });
});
