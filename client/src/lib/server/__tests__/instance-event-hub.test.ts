/**
 * Tests for instance-event-hub.ts — singleton hub with ref-counted per-instance
 * subscriptions, snapshot-safe dispatch, and automatic reconnection.
 */

import { vi, describe, it, expect, beforeEach, afterEach } from "vitest";

// Mock process-manager to prevent real instance lookups and recovery
vi.mock("@/lib/server/process-manager", () => ({
  getInstance: vi.fn(() => undefined),
  _recoveryComplete: Promise.resolve(),
}));

// Mock opencode-client to prevent real SDK calls
vi.mock("@/lib/server/opencode-client", () => ({
  getClientForInstance: vi.fn(() => {
    throw new Error("No instance in test");
  }),
}));

import { addListener, removeAllListeners, _resetForTests } from "@/lib/server/instance-event-hub";
import * as processManager from "@/lib/server/process-manager";
import * as opencodeClient from "@/lib/server/opencode-client";

// ─── Helpers ──────────────────────────────────────────────────────────────────

function createMockEventStream() {
  const events: unknown[] = [];
  let resolve: (() => void) | null = null;
  let done = false;

  const stream: AsyncIterable<unknown> = {
    [Symbol.asyncIterator]() {
      let index = 0;
      return {
        async next() {
          while (index >= events.length && !done) {
            await new Promise<void>((r) => {
              resolve = r;
            });
          }
          if (index >= events.length && done) {
            return { done: true as const, value: undefined };
          }
          return { done: false as const, value: events[index++] };
        },
      };
    },
  };

  return {
    stream,
    push(event: unknown) {
      events.push(event);
      resolve?.();
      resolve = null;
    },
    end() {
      done = true;
      resolve?.();
      resolve = null;
    },
  };
}

function setupInstance(directory = "/test") {
  vi.mocked(processManager.getInstance).mockReturnValue({
    directory,
    status: "running",
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
  } as any);
}

function setupSubscribe(stream: AsyncIterable<unknown>) {
  vi.mocked(opencodeClient.getClientForInstance).mockReturnValue({
    event: {
      subscribe: vi.fn().mockResolvedValue({ stream }),
    },
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
  } as any);
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

describe("instance-event-hub", () => {
  describe("addListener with dead/unknown instance", () => {
    it("AddListenerWithDeadInstanceIsNoOp", () => {
      vi.mocked(processManager.getInstance).mockReturnValue(undefined);
      const listener = vi.fn();
      let unsubscribe!: () => void;
      expect(() => {
        unsubscribe = addListener("inst-dead", listener);
      }).not.toThrow();
      expect(() => unsubscribe()).not.toThrow();
      expect(listener).not.toHaveBeenCalled();
    });
  });

  describe("subscription lifecycle", () => {
    it("FirstListenerTriggersSubscription", async () => {
      const mock = createMockEventStream();
      setupInstance();
      setupSubscribe(mock.stream);

      const listener = vi.fn();
      addListener("inst-1", listener);

      // Give async subscription a chance to start
      await new Promise((r) => setTimeout(r, 20));

      expect(opencodeClient.getClientForInstance).toHaveBeenCalledWith("inst-1");
      mock.end();
    });

    it("SecondListenerReusesExistingSubscription", async () => {
      const mock = createMockEventStream();
      setupInstance();
      setupSubscribe(mock.stream);

      const listener1 = vi.fn();
      const listener2 = vi.fn();
      addListener("inst-2", listener1);
      addListener("inst-2", listener2);

      // Give async subscription a chance to start
      await new Promise((r) => setTimeout(r, 20));

      // Only one subscribe call despite two listeners
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      const subscribeFn = (vi.mocked(opencodeClient.getClientForInstance).mock.results[0]?.value as any)?.event?.subscribe;
      expect(subscribeFn).toHaveBeenCalledTimes(1);
      mock.end();
    });

    it("EventsDispatchedToAllRegisteredListeners", async () => {
      const mock = createMockEventStream();
      setupInstance();
      setupSubscribe(mock.stream);

      const listener1 = vi.fn();
      const listener2 = vi.fn();
      addListener("inst-3", listener1);
      addListener("inst-3", listener2);

      mock.push({ type: "session.status", properties: { sessionID: "s1", status: { type: "busy" } } });

      await vi.waitFor(() => {
        expect(listener1).toHaveBeenCalledTimes(1);
        expect(listener2).toHaveBeenCalledTimes(1);
      });

      expect(listener1).toHaveBeenCalledWith({
        type: "session.status",
        properties: { sessionID: "s1", status: { type: "busy" } },
      });
      expect(listener2).toHaveBeenCalledWith({
        type: "session.status",
        properties: { sessionID: "s1", status: { type: "busy" } },
      });

      mock.end();
    });

    it("RemovingLastListenerTearsDownSubscription", async () => {
      const mock = createMockEventStream();
      setupInstance();
      setupSubscribe(mock.stream);

      const listener = vi.fn();
      const unsubscribe = addListener("inst-4", listener);

      await new Promise((r) => setTimeout(r, 20));

      unsubscribe();

      // After unsubscribe, further events should not reach listener
      mock.push({ type: "ping", properties: {} });
      await new Promise((r) => setTimeout(r, 20));

      expect(listener).not.toHaveBeenCalled();
      mock.end();
    });

    it("RemoveAllListenersAbortsStreamAndRemovesAllListeners", async () => {
      const mock = createMockEventStream();
      setupInstance();
      setupSubscribe(mock.stream);

      const listener1 = vi.fn();
      const listener2 = vi.fn();
      addListener("inst-5", listener1);
      addListener("inst-5", listener2);

      await new Promise((r) => setTimeout(r, 20));

      removeAllListeners("inst-5");

      mock.push({ type: "ping", properties: {} });
      await new Promise((r) => setTimeout(r, 20));

      expect(listener1).not.toHaveBeenCalled();
      expect(listener2).not.toHaveBeenCalled();
      mock.end();
    });
  });

  describe("_resetForTests", () => {
    it("ClearsAllStateWithoutError", () => {
      setupInstance();
      setupSubscribe(createMockEventStream().stream);
      addListener("inst-reset", vi.fn());
      expect(() => _resetForTests()).not.toThrow();
    });
  });

  describe("snapshot-safe dispatch (Blocker 1)", () => {
    it("ListenerCallingUnsubscribeDuringDispatchDoesNotCauseErrorsOrSkipOtherListeners", async () => {
      const mock = createMockEventStream();
      setupInstance();
      setupSubscribe(mock.stream);

      const listener2 = vi.fn();
      // eslint-disable-next-line prefer-const -- must be declared before use in listener1 closure
      let unsubscribe1!: () => void;

      // listener1 calls its own unsubscribe during handling
      const listener1 = vi.fn(() => {
        unsubscribe1();
      });

      unsubscribe1 = addListener("inst-snap", listener1);
      addListener("inst-snap", listener2);

      mock.push({ type: "test", properties: {} });

      await vi.waitFor(() => {
        expect(listener1).toHaveBeenCalledTimes(1);
        expect(listener2).toHaveBeenCalledTimes(1);
      });

      // Second event: listener1 should no longer receive it (was removed)
      mock.push({ type: "test2", properties: {} });
      await vi.waitFor(() => {
        expect(listener2).toHaveBeenCalledTimes(2);
      });
      // listener1 still at 1 — it was removed
      expect(listener1).toHaveBeenCalledTimes(1);

      mock.end();
    });

    it("ListenerCallingAddListenerDuringDispatchDoesNotReceiveCurrentEvent", async () => {
      const mock = createMockEventStream();
      setupInstance();
      setupSubscribe(mock.stream);

      const newListener = vi.fn();
      const listener1 = vi.fn(() => {
        addListener("inst-reentrant", newListener);
      });

      addListener("inst-reentrant", listener1);

      mock.push({ type: "test", properties: {} });

      await vi.waitFor(() => {
        expect(listener1).toHaveBeenCalledTimes(1);
      });

      // newListener should NOT have received the current event
      expect(newListener).not.toHaveBeenCalled();

      // On subsequent event, newListener DOES receive it
      mock.push({ type: "test2", properties: {} });
      await vi.waitFor(() => {
        expect(newListener).toHaveBeenCalledTimes(1);
      });

      mock.end();
    });
  });

  describe("distinct unsubscribe functions (Blocker 3)", () => {
    it("TwoAddListenerCallsReturnDistinctUnsubscribeFunctions", async () => {
      const mock = createMockEventStream();
      setupInstance();
      setupSubscribe(mock.stream);

      const listener1 = vi.fn();
      const listener2 = vi.fn();
      const unsub1 = addListener("inst-dist", listener1);
      const unsub2 = addListener("inst-dist", listener2);

      expect(unsub1).not.toBe(unsub2);

      // Calling unsub1 should NOT affect listener2
      unsub1();

      mock.push({ type: "test", properties: {} });
      await vi.waitFor(() => {
        expect(listener2).toHaveBeenCalledTimes(1);
      });
      expect(listener1).not.toHaveBeenCalled();

      mock.end();
      unsub2();
    });

    it("UnsubscribeIsIdempotent", async () => {
      const mock = createMockEventStream();
      setupInstance();
      setupSubscribe(mock.stream);

      const listener = vi.fn();
      const unsubscribe = addListener("inst-idem", listener);

      unsubscribe();
      expect(() => unsubscribe()).not.toThrow();
      expect(() => unsubscribe()).not.toThrow();

      mock.end();
    });
  });

  describe("automatic reconnection (Blocker 2)", () => {
    it("StreamEndWithListenersStillRegisteredTriggersReconnect", async () => {
      const mock1 = createMockEventStream();
      const mock2 = createMockEventStream();

      setupInstance();
      let subscribeCallCount = 0;

      vi.mocked(opencodeClient.getClientForInstance).mockImplementation(() => ({
        event: {
          subscribe: vi.fn().mockImplementation(() => {
            subscribeCallCount++;
            if (subscribeCallCount === 1) {
              return Promise.resolve({ stream: mock1.stream });
            }
            return Promise.resolve({ stream: mock2.stream });
          }),
        },
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
      } as any));

      const listener = vi.fn();
      const unsubscribe = addListener("inst-reconnect", listener);

      // Set very short reconnect delay
      process.env.WEAVE_SUBSCRIBE_TIMEOUT_MS = "30000";

      // Ensure first connection is established
      await vi.waitFor(() => {
        expect(subscribeCallCount).toBe(1);
      });

      // End first stream — should trigger reconnect
      mock1.end();

      // Wait for reconnect
      await vi.waitFor(() => {
        expect(subscribeCallCount).toBe(2);
      }, { timeout: 5000 });

      // Events from second stream reach the listener
      mock2.push({ type: "reconnected", properties: {} });
      await vi.waitFor(() => {
        expect(listener).toHaveBeenCalledWith({ type: "reconnected", properties: {} });
      });

      mock2.end();
      unsubscribe();
    });

    it("ReconnectStopsIfAllListenersRemovedDuringBackoff", async () => {
      const mock1 = createMockEventStream();

      setupInstance();
      let subscribeCallCount = 0;

      vi.mocked(opencodeClient.getClientForInstance).mockImplementation(() => ({
        event: {
          subscribe: vi.fn().mockImplementation(() => {
            subscribeCallCount++;
            if (subscribeCallCount === 1) {
              return Promise.resolve({ stream: mock1.stream });
            }
            // Should not reach here
            return new Promise(() => {});
          }),
        },
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
      } as any));

      const listener = vi.fn();
      const unsubscribe = addListener("inst-cancel-reconnect", listener);

      await vi.waitFor(() => {
        expect(subscribeCallCount).toBe(1);
      });

      // End the stream — would trigger reconnect with 1s backoff
      mock1.end();

      // Immediately remove listener — reconnect should be cancelled
      unsubscribe();

      // Wait and verify no second subscribe attempt
      await new Promise((r) => setTimeout(r, 200));
      expect(subscribeCallCount).toBe(1);
    });

    it("RemoveAllListenersCancelsPendingReconnect", async () => {
      const mock1 = createMockEventStream();

      setupInstance();
      let subscribeCallCount = 0;

      vi.mocked(opencodeClient.getClientForInstance).mockImplementation(() => ({
        event: {
          subscribe: vi.fn().mockImplementation(() => {
            subscribeCallCount++;
            if (subscribeCallCount === 1) {
              return Promise.resolve({ stream: mock1.stream });
            }
            return new Promise(() => {});
          }),
        },
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
      } as any));

      addListener("inst-rmall", vi.fn());
      addListener("inst-rmall", vi.fn());

      await vi.waitFor(() => {
        expect(subscribeCallCount).toBe(1);
      });

      // End stream — would trigger reconnect
      mock1.end();

      // Force-remove all via removeAllListeners
      removeAllListeners("inst-rmall");

      await new Promise((r) => setTimeout(r, 200));
      expect(subscribeCallCount).toBe(1);
    });

    it("ReconnectStopsIfInstanceBecomesDead", async () => {
      const mock1 = createMockEventStream();

      let callCount = 0;
      vi.mocked(processManager.getInstance).mockImplementation(() => {
        callCount++;
        if (callCount <= 2) {
          // First call (addListener) and first subscribe — alive
          return { directory: "/test", status: "running" } as ReturnType<typeof processManager.getInstance>;
        }
        // Subsequent calls (during reconnect check) — dead
        return { directory: "/test", status: "dead" } as ReturnType<typeof processManager.getInstance>;
      });

      vi.mocked(opencodeClient.getClientForInstance).mockReturnValue({
        event: {
          subscribe: vi.fn().mockResolvedValue({ stream: mock1.stream }),
        },
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
      } as any);

      const listener = vi.fn();
      addListener("inst-dead-reconnect", listener);

      await new Promise((r) => setTimeout(r, 20));

      // End stream — hub will check instance status and find it dead
      mock1.end();

      // Give time for the reconnect check
      await new Promise((r) => setTimeout(r, 100));

      // Subscribe should only have been called once (no reconnect to dead instance)
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      const subscribeFn = (vi.mocked(opencodeClient.getClientForInstance).mock.results[0]?.value as any)?.event?.subscribe;
      expect(subscribeFn).toHaveBeenCalledTimes(1);
    });

    it("AfterReconnectSuccessExistingListenersReceiveEventsFromNewStream", async () => {
      const mock1 = createMockEventStream();
      const mock2 = createMockEventStream();

      setupInstance();
      let subscribeCallCount = 0;

      vi.mocked(opencodeClient.getClientForInstance).mockImplementation(() => ({
        event: {
          subscribe: vi.fn().mockImplementation(() => {
            subscribeCallCount++;
            if (subscribeCallCount === 1) return Promise.resolve({ stream: mock1.stream });
            return Promise.resolve({ stream: mock2.stream });
          }),
        },
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
      } as any));

      const listener = vi.fn();
      const unsubscribe = addListener("inst-after-reconnect", listener);

      await vi.waitFor(() => expect(subscribeCallCount).toBe(1));

      // Push one event on first stream
      mock1.push({ type: "before", properties: {} });
      await vi.waitFor(() => expect(listener).toHaveBeenCalledTimes(1));

      // End first stream
      mock1.end();

      // Wait for reconnect
      await vi.waitFor(() => expect(subscribeCallCount).toBe(2), { timeout: 5000 });

      // Same listener receives events from new stream without re-registering
      mock2.push({ type: "after", properties: {} });
      await vi.waitFor(() => expect(listener).toHaveBeenCalledTimes(2));

      expect(listener).toHaveBeenLastCalledWith({ type: "after", properties: {} });

      mock2.end();
      unsubscribe();
    });
  });
});
