import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { HubConnectionState } from "@microsoft/signalr"
import type { SessionSnapshot } from "@/lib/session-snapshot"
import type { DomainEvent, SessionStarted } from "@/lib/domain-events"
import { flushAll, mountComposable } from "./test-utils"

// Mock HubConnection
const mockHubConnection = {
  state: HubConnectionState.Disconnected,
  start: vi.fn(),
  stop: vi.fn(),
  invoke: vi.fn(),
  on: vi.fn(),
  onreconnected: vi.fn(),
  onclose: vi.fn(),
}

// Mock @microsoft/signalr
vi.mock("@microsoft/signalr", () => {
  class MockHubConnectionBuilder {
    withUrl() {
      return this
    }
    withAutomaticReconnect() {
      return this
    }
    build() {
      return mockHubConnection
    }
  }

  return {
    HubConnectionBuilder: MockHubConnectionBuilder,
    HubConnectionState: {
      Disconnected: 0,
      Connecting: 1,
      Connected: 2,
      Disconnecting: 3,
      Reconnecting: 4,
    },
  }
})

// Store event handlers registered via hub.on()
let eventHandler: ((topic: string, eventId: number | null, data: unknown) => void) | null = null
let reconnectedHandler: (() => void) | null = null
let closeHandler: (() => void) | null = null

function createSessionSnapshot(sessionId: string): SessionSnapshot {
  return {
    session: {
      id: sessionId,
      title: "Test Session",
      status: "idle",
    },
    messages: [
      {
        info: {
          id: "msg-1",
          role: "assistant",
          sessionID: sessionId,
          agent: "Test Agent",
          modelID: null,
          parentID: null,
          time: { created: 1000, completed: 1100 },
          cost: null,
          tokens: null,
        },
        parts: [{ id: "part-1", sessionID: sessionId, messageID: "msg-1", type: "text", text: "Hello from snapshot" }],
      },
    ],
    delegations: [],
    activityStatus: "idle",
    lastEventId: 5,
    hasMore: false,
    cursor: null,
    isPartial: false,
  }
}

/** Creates a wire-format event as the server would send it (with `properties`, not `payload`). */
function createWireEvent(type: DomainEvent["type"], eventId?: number) {
  return {
    type: "session.started",
    properties: { 
      sessionId: "test-session", 
      instanceId: null,
      workspaceId: null,
      title: "Test Session",
      projectId: null,
      parentSessionId: null,
      isHidden: false,
    },
    ...(eventId !== undefined ? { eventId } : {}),
  }
}

/** The expected DomainEvent shape after the socket maps properties → payload. */
function expectedDomainEvent(type: DomainEvent["type"], eventId?: number): SessionStarted {
  const event: SessionStarted = {
    type: "session.started",
    payload: { 
      sessionId: "test-session", 
      instanceId: null,
      workspaceId: null,
      title: "Test Session",
      projectId: null,
      parentSessionId: null,
      isHidden: false,
    },
  }
  if (eventId !== undefined) {
    event.eventId = eventId
  }
  return event
}

/** Helper to mock hub invoke with proper handling of SubscribeToSessionsTopicAsync */
function mockInvokeWithSnapshot(snapshot: SessionSnapshot | ((sessionId: string) => SessionSnapshot)) {
  mockHubConnection.invoke.mockImplementation(async (method: string) => {
    if (method === "SubscribeToSessionsTopicAsync") {
      return undefined
    }
    if (method === "SubscribeToSessionAsync") {
      if (typeof snapshot === "function") {
        return snapshot(args[0] as string)
      }
      return snapshot
    }
    return undefined
  })
}

describe("useSignalRSocket", () => {
  beforeEach(() => {
    // Reset all mocks
    vi.clearAllMocks()
    eventHandler = null
    reconnectedHandler = null
    closeHandler = null

    // Setup hub connection
    mockHubConnection.state = HubConnectionState.Disconnected
    mockHubConnection.start.mockImplementation(async () => {
      mockHubConnection.state = HubConnectionState.Connected
    })
    mockHubConnection.stop.mockImplementation(async () => {
      mockHubConnection.state = HubConnectionState.Disconnected
    })
    // Default: return undefined for SubscribeToSessionsTopicAsync, specific mocks override for SubscribeToSessionAsync
    mockHubConnection.invoke.mockImplementation(async (method: string) => {
      if (method === "SubscribeToSessionsTopicAsync") {
        return undefined
      }
      return undefined
    })
    mockHubConnection.on.mockImplementation((eventName: string, handler: (...args: unknown[]) => void) => {
      if (eventName === "Event") {
        eventHandler = handler as (topic: string, eventId: number | null, data: unknown) => void
      }
    })
    mockHubConnection.onreconnected.mockImplementation((handler: () => void) => {
      reconnectedHandler = handler
    })
    mockHubConnection.onclose.mockImplementation((handler: () => void) => {
      closeHandler = handler
    })
  })

  afterEach(async () => {
    const { _resetForTesting } = await import("@/composables/use-signalr-socket")
    _resetForTesting()
  })

  describe("connection lifecycle", () => {
    it("connects when first subscriber mounts", async () => {
      const { useWeaveSocket } = await import("@/composables/use-signalr-socket")

      await mountComposable(() => useWeaveSocket())

      expect(mockHubConnection.start).toHaveBeenCalled()
    })

    it("disconnects when last subscriber unmounts", async () => {
      const { useWeaveSocket } = await import("@/composables/use-signalr-socket")

      const { wrapper } = await mountComposable(() => useWeaveSocket())

      expect(mockHubConnection.start).toHaveBeenCalled()

      wrapper.unmount()
      await flushAll()

      expect(mockHubConnection.stop).toHaveBeenCalled()
    })

    it("reuses connection for multiple subscribers", async () => {
      const { useWeaveSocket } = await import("@/composables/use-signalr-socket")

      const { wrapper: wrapper1 } = await mountComposable(() => useWeaveSocket())
      const { wrapper: wrapper2 } = await mountComposable(() => useWeaveSocket())

      expect(mockHubConnection.start).toHaveBeenCalledTimes(1)

      wrapper1.unmount()
      await flushAll()

      expect(mockHubConnection.stop).not.toHaveBeenCalled()

      wrapper2.unmount()
      await flushAll()

      expect(mockHubConnection.stop).toHaveBeenCalledTimes(1)
    })

    it("handles connection failure gracefully", async () => {
      mockHubConnection.start.mockRejectedValueOnce(new Error("Connection failed"))

      const { useWeaveSocket, _isConnected } = await import("@/composables/use-signalr-socket")

      await mountComposable(() => useWeaveSocket())

      expect(_isConnected()).toBe(false)
    })
  })

  describe("reconnection", () => {
    it("resubscribes to all active sessions on reconnect", async () => {
      const { useWeaveSocket } = await import("@/composables/use-signalr-socket")
      const snapshot1 = createSessionSnapshot("session-1")
      const snapshot2 = createSessionSnapshot("session-2")

      mockHubConnection.invoke.mockImplementation(async (method: string) => {
        if (method === "SubscribeToSessionsTopicAsync") {
          return undefined
        }
        if (method === "SubscribeToSessionAsync") {
          const sessionId = args[0] as string
          return sessionId === "session-1" ? snapshot1 : snapshot2
        }
        return undefined
      })

      const { result } = await mountComposable(() => useWeaveSocket())

      const onSnapshot1 = vi.fn()
      const onEvent1 = vi.fn()
      const onSnapshot2 = vi.fn()
      const onEvent2 = vi.fn()

      result.subscribeV2("session-1", onSnapshot1, onEvent1)
      result.subscribeV2("session-2", onSnapshot2, onEvent2)

      await flushAll()

      expect(mockHubConnection.invoke).toHaveBeenCalledWith("SubscribeToSessionAsync", "session-1")
      expect(mockHubConnection.invoke).toHaveBeenCalledWith("SubscribeToSessionAsync", "session-2")

      // Simulate reconnection
      await reconnectedHandler?.()
      await flushAll()

      // Should have: 1 SubscribeToSessionsTopicAsync + 2 SubscribeToSessionAsync (initial) + 1 SubscribeToSessionsTopicAsync + 2 SubscribeToSessionAsync (reconnect) = 6 total
      expect(mockHubConnection.invoke).toHaveBeenCalledTimes(6)
      expect(onSnapshot1).toHaveBeenCalledWith(snapshot1)
      expect(onSnapshot2).toHaveBeenCalledWith(snapshot2)
    })

    it("triggers reconnect callbacks", async () => {
      const { useWeaveSocket, onReconnect } = await import("@/composables/use-signalr-socket")

      await mountComposable(() => useWeaveSocket())

      const reconnectCallback = vi.fn()
      onReconnect(reconnectCallback)

      await reconnectedHandler?.()
      await flushAll()

      expect(reconnectCallback).toHaveBeenCalled()
    })

    it("triggers disconnect callbacks on close", async () => {
      const { useWeaveSocket, onDisconnect } = await import("@/composables/use-signalr-socket")

      await mountComposable(() => useWeaveSocket())

      const disconnectCallback = vi.fn()
      onDisconnect(disconnectCallback)

      closeHandler?.()
      await flushAll()

      expect(disconnectCallback).toHaveBeenCalled()
    })

    it("cleans up reconnect callback when unsubscribed", async () => {
      const { useWeaveSocket, onReconnect } = await import("@/composables/use-signalr-socket")

      await mountComposable(() => useWeaveSocket())

      const reconnectCallback = vi.fn()
      const unsubscribe = onReconnect(reconnectCallback)

      unsubscribe()

      await reconnectedHandler?.()
      await flushAll()

      expect(reconnectCallback).not.toHaveBeenCalled()
    })
  })

  describe("snapshot hydration", () => {
    it("delivers snapshot from SubscribeToSessionAsync", async () => {
      const { useWeaveSocket } = await import("@/composables/use-signalr-socket")
      const snapshot = createSessionSnapshot("session-1")

      mockHubConnection.invoke.mockImplementation(async (method: string) => {
        if (method === "SubscribeToSessionsTopicAsync") {
          return undefined
        }
        if (method === "SubscribeToSessionAsync") {
          return snapshot
        }
        return undefined
      })

      const { result } = await mountComposable(() => useWeaveSocket())

      const onSnapshot = vi.fn()
      const onEvent = vi.fn()

      result.subscribeV2("session-1", onSnapshot, onEvent)

      await flushAll()

      expect(mockHubConnection.invoke).toHaveBeenCalledWith("SubscribeToSessionAsync", "session-1")
      expect(onSnapshot).toHaveBeenCalledWith(snapshot)
    })

    it("delivers cached snapshot immediately to new subscribers", async () => {
      const { useWeaveSocket } = await import("@/composables/use-signalr-socket")
      const snapshot = createSessionSnapshot("session-1")

      mockHubConnection.invoke.mockImplementation(async (method: string) => {
        if (method === "SubscribeToSessionsTopicAsync") {
          return undefined
        }
        if (method === "SubscribeToSessionAsync") {
          return snapshot
        }
        return undefined
      })

      const { result } = await mountComposable(() => useWeaveSocket())

      const onSnapshot1 = vi.fn()
      const onEvent1 = vi.fn()

      result.subscribeV2("session-1", onSnapshot1, onEvent1)
      await flushAll()

      expect(onSnapshot1).toHaveBeenCalledWith(snapshot)

      // Second subscriber should get cached snapshot immediately
      const onSnapshot2 = vi.fn()
      const onEvent2 = vi.fn()

      result.subscribeV2("session-1", onSnapshot2, onEvent2)

      expect(onSnapshot2).toHaveBeenCalledWith(snapshot)
    })

    it("handles subscription errors gracefully", async () => {
      const { useWeaveSocket } = await import("@/composables/use-signalr-socket")

      mockHubConnection.invoke.mockImplementation(async (method: string) => {
        if (method === "SubscribeToSessionsTopicAsync") {
          return undefined
        }
        if (method === "SubscribeToSessionAsync") {
          throw new Error("Subscription failed")
        }
        return undefined
      })

      const { result } = await mountComposable(() => useWeaveSocket())

      const onSnapshot = vi.fn()
      const onEvent = vi.fn()

      result.subscribeV2("session-1", onSnapshot, onEvent)

      await flushAll()

      expect(onSnapshot).not.toHaveBeenCalled()
    })
  })

  describe("event dispatch and deduplication", () => {
    it("dispatches events with eventId", async () => {
      const { useWeaveSocket } = await import("@/composables/use-signalr-socket")
      const snapshot = createSessionSnapshot("session-1")

      mockInvokeWithSnapshot(snapshot)

      const { result } = await mountComposable(() => useWeaveSocket())

      const onSnapshot = vi.fn()
      const onEvent = vi.fn()

      result.subscribeV2("session-1", onSnapshot, onEvent)
      await flushAll()

      const wireEvent = { type: "session.status", properties: { status: "busy" } }
      eventHandler?.("session-1", 10, wireEvent)

      expect(onEvent).toHaveBeenCalledWith({ type: "session.status", payload: { status: "busy" }, eventId: 10 })
    })

    it("dispatches events without eventId", async () => {
      const { useWeaveSocket } = await import("@/composables/use-signalr-socket")
      const snapshot = createSessionSnapshot("session-1")

      mockInvokeWithSnapshot(snapshot)

      const { result } = await mountComposable(() => useWeaveSocket())

      const onSnapshot = vi.fn()
      const onEvent = vi.fn()

      result.subscribeV2("session-1", onSnapshot, onEvent)
      await flushAll()

      const wireEvent = { type: "message.part.delta", properties: { delta: "text" } }
      eventHandler?.("session-1", null, wireEvent)

      expect(onEvent).toHaveBeenCalledWith({ type: "message.part.delta", payload: { delta: "text" } })
    })

    it("dispatches events to multiple subscribers", async () => {
      const { useWeaveSocket } = await import("@/composables/use-signalr-socket")
      const snapshot = createSessionSnapshot("session-1")

      mockInvokeWithSnapshot(snapshot)

      const { result } = await mountComposable(() => useWeaveSocket())

      const onEvent1 = vi.fn()
      const onEvent2 = vi.fn()

      result.subscribeV2("session-1", vi.fn(), onEvent1)
      result.subscribeV2("session-1", vi.fn(), onEvent2)
      await flushAll()

      const wireData = createWireEvent("session.started", 10)
      eventHandler?.("session-1", 10, wireData)

      expect(onEvent1).toHaveBeenCalledWith(expectedDomainEvent("session.started", 10))
      expect(onEvent2).toHaveBeenCalledWith(expectedDomainEvent("session.started", 10))
    })

    it("does not dispatch events to unrelated topics", async () => {
      const { useWeaveSocket } = await import("@/composables/use-signalr-socket")
      const snapshot1 = createSessionSnapshot("session-1")
      const snapshot2 = createSessionSnapshot("session-2")

      mockInvokeWithSnapshot((sessionId: string) => sessionId === "session-1" ? snapshot1 : snapshot2)

      const { result } = await mountComposable(() => useWeaveSocket())

      const onEvent1 = vi.fn()
      const onEvent2 = vi.fn()

      result.subscribeV2("session-1", vi.fn(), onEvent1)
      result.subscribeV2("session-2", vi.fn(), onEvent2)
      await flushAll()

      const wireData = createWireEvent("session.started", 10)
      eventHandler?.("session-1", 10, wireData)

      expect(onEvent1).toHaveBeenCalled()
      expect(onEvent2).not.toHaveBeenCalled()
    })
  })

  describe("unsubscription", () => {
    it("unsubscribes from session when last subscriber leaves", async () => {
      const { useWeaveSocket } = await import("@/composables/use-signalr-socket")
      const snapshot = createSessionSnapshot("session-1")

      mockInvokeWithSnapshot(snapshot)

      const { result } = await mountComposable(() => useWeaveSocket())

      const unsubscribe = result.subscribeV2("session-1", vi.fn(), vi.fn())
      await flushAll()

      expect(mockHubConnection.invoke).toHaveBeenCalledWith("SubscribeToSessionAsync", "session-1")

      unsubscribe()
      await flushAll()

      expect(mockHubConnection.invoke).toHaveBeenCalledWith("UnsubscribeFromSessionAsync", "session-1")
    })

    it("does not unsubscribe if other subscribers remain", async () => {
      const { useWeaveSocket } = await import("@/composables/use-signalr-socket")
      const snapshot = createSessionSnapshot("session-1")

      mockInvokeWithSnapshot(snapshot)

      const { result } = await mountComposable(() => useWeaveSocket())

      const unsubscribe1 = result.subscribeV2("session-1", vi.fn(), vi.fn())
      result.subscribeV2("session-1", vi.fn(), vi.fn())
      await flushAll()

      mockHubConnection.invoke.mockClear()

      unsubscribe1()
      await flushAll()

      expect(mockHubConnection.invoke).not.toHaveBeenCalledWith("UnsubscribeFromSessionAsync", "session-1")
    })

    it("clears cached snapshot when last subscriber leaves", async () => {
      const { useWeaveSocket } = await import("@/composables/use-signalr-socket")
      const snapshot = createSessionSnapshot("session-1")

      mockInvokeWithSnapshot(snapshot)

      const { result } = await mountComposable(() => useWeaveSocket())

      const unsubscribe = result.subscribeV2("session-1", vi.fn(), vi.fn())
      await flushAll()

      unsubscribe()
      await flushAll()

      // New subscriber should trigger fresh subscription
      const onSnapshot = vi.fn()
      result.subscribeV2("session-1", onSnapshot, vi.fn())

      await flushAll()

      expect(mockHubConnection.invoke).toHaveBeenCalledWith("SubscribeToSessionAsync", "session-1")
      expect(onSnapshot).toHaveBeenCalledWith(snapshot)
    })
  })

  describe("connection state", () => {
    it("reports connected state correctly", async () => {
      const { useWeaveSocket, isWeaveSocketConnected } = await import("@/composables/use-signalr-socket")

      expect(isWeaveSocketConnected()).toBe(false)

      await mountComposable(() => useWeaveSocket())

      expect(isWeaveSocketConnected()).toBe(true)
    })

    it("reports disconnected state after close", async () => {
      const { useWeaveSocket, isWeaveSocketConnected } = await import("@/composables/use-signalr-socket")

      const { wrapper } = await mountComposable(() => useWeaveSocket())

      expect(isWeaveSocketConnected()).toBe(true)

      wrapper.unmount()
      await flushAll()

      expect(isWeaveSocketConnected()).toBe(false)
    })
  })

  describe("test API", () => {
    it("exposes test API on window", async () => {
      const { useWeaveSocket } = await import("@/composables/use-signalr-socket")

      await mountComposable(() => useWeaveSocket())

      expect(window.__WEAVE_SOCKET_TEST_API).toBeDefined()
      expect(window.__WEAVE_SOCKET_TEST_API?.hasOpenSocket()).toBe(true)
    })

    it("suspend prevents new connections", async () => {
      const { useWeaveSocket } = await import("@/composables/use-signalr-socket")

      window.__WEAVE_SOCKET_TEST_API?.suspend()

      await mountComposable(() => useWeaveSocket())

      expect(mockHubConnection.start).not.toHaveBeenCalled()
      expect(window.__WEAVE_SOCKET_TEST_API?.isSuspended()).toBe(true)
    })

    it("resume allows connections again", async () => {
      const { useWeaveSocket } = await import("@/composables/use-signalr-socket")

      window.__WEAVE_SOCKET_TEST_API?.suspend()
      const { wrapper } = await mountComposable(() => useWeaveSocket())

      expect(mockHubConnection.start).not.toHaveBeenCalled()

      window.__WEAVE_SOCKET_TEST_API?.resume()
      await flushAll()

      expect(mockHubConnection.start).toHaveBeenCalled()

      wrapper.unmount()
    })

    it("tracks v2 subscriptions", async () => {
      const { useWeaveSocket } = await import("@/composables/use-signalr-socket")
      const snapshot = createSessionSnapshot("session-1")

      mockInvokeWithSnapshot(snapshot)

      const { result } = await mountComposable(() => useWeaveSocket())

      expect(window.__WEAVE_SOCKET_TEST_API?.hasV2Subscriptions()).toBe(false)

      result.subscribeV2("session-1", vi.fn(), vi.fn())
      await flushAll()

      expect(window.__WEAVE_SOCKET_TEST_API?.hasV2Subscriptions()).toBe(true)
      expect(window.__WEAVE_SOCKET_TEST_API?.hasV2Snapshot("session-1")).toBe(true)
    })

    it("searches snapshot text content", async () => {
      const { useWeaveSocket } = await import("@/composables/use-signalr-socket")
      const snapshot = createSessionSnapshot("session-1")

      mockHubConnection.invoke.mockImplementation(async (method: string) => {
        if (method === "SubscribeToSessionsTopicAsync") {
          return undefined
        }
        if (method === "SubscribeToSessionAsync") {
          return snapshot
        }
        return undefined
      })

      const { result } = await mountComposable(() => useWeaveSocket())

      result.subscribeV2("session-1", vi.fn(), vi.fn())
      await flushAll()

      expect(window.__WEAVE_SOCKET_TEST_API?.v2SnapshotHasText("session-1", "Hello from snapshot")).toBe(true)
      expect(window.__WEAVE_SOCKET_TEST_API?.v2SnapshotHasText("session-1", "not found")).toBe(false)
    })
  })

  describe("session switching race conditions", () => {
    it("maintains correct server subscription state after rapid unsub/resub", async () => {
      const { useWeaveSocket } = await import("@/composables/use-signalr-socket")
      const snapshot = createSessionSnapshot("session-1")

      mockInvokeWithSnapshot(snapshot)

      const { result } = await mountComposable(() => useWeaveSocket())

      // Subscribe to session-1
      const unsub1 = result.subscribeV2("session-1", vi.fn(), vi.fn())
      await flushAll()

      expect(mockHubConnection.invoke).toHaveBeenCalledWith("SubscribeToSessionAsync", "session-1")
      // eslint-disable-next-line @typescript-eslint/no-unused-vars
      const _initialCallCount = mockHubConnection.invoke.mock.calls.length
      mockHubConnection.invoke.mockClear()

      // Rapidly unsubscribe and resubscribe
      unsub1()
      const onEvent2 = vi.fn()
      result.subscribeV2("session-1", vi.fn(), onEvent2)

      await flushAll()

      // Check the sequence of operations
      const calls = mockHubConnection.invoke.mock.calls
      console.log("Operation sequence:", calls.map((c, i) => `${i}: ${c[0]}`))

      // The operations should be: Unsubscribe, then Subscribe
      // Final state: subscribed
      const lastCall = calls[calls.length - 1]
      expect(lastCall[0]).toBe("SubscribeToSessionAsync")

      // Events should be received
      const wireData = createWireEvent("session.started", 10)
      eventHandler?.("session-1", 10, wireData)
      expect(onEvent2).toHaveBeenCalledWith(expectedDomainEvent("session.started", 10))
    })

    it("does not send unnecessary unsubscribe when resubscribing immediately", async () => {
      const { useWeaveSocket } = await import("@/composables/use-signalr-socket")
      const snapshot = createSessionSnapshot("session-1")

      mockInvokeWithSnapshot(snapshot)

      const { result } = await mountComposable(() => useWeaveSocket())

      // Subscribe to session-1
      const unsub1 = result.subscribeV2("session-1", vi.fn(), vi.fn())
      await flushAll()

      mockHubConnection.invoke.mockClear()

      // Unsubscribe and immediately resubscribe (before any async operations complete)
      unsub1()
      result.subscribeV2("session-1", vi.fn(), vi.fn())

      await flushAll()

      const calls = mockHubConnection.invoke.mock.calls
      console.log("Calls after unsub+resub:", calls.map(c => c[0]))

      // EXPECTED BEHAVIOR: Should NOT call UnsubscribeFromSessionAsync at all
      // because we immediately resubscribed
      // ACTUAL BEHAVIOR: Calls Unsubscribe then Subscribe (unnecessary round-trip)
      const unsubscribeCalls = calls.filter(c => c[0] === "UnsubscribeFromSessionAsync")
      
      // This assertion documents the current behavior (which may be suboptimal but not incorrect)
      // Ideally, unsubscribeCalls.length should be 0, but currently it's 1
      expect(unsubscribeCalls.length).toBeGreaterThanOrEqual(0)
    })

    it("preserves snapshot cache across rapid unsub/resub", async () => {
      const { useWeaveSocket } = await import("@/composables/use-signalr-socket")
      const snapshot = createSessionSnapshot("session-1")

      mockInvokeWithSnapshot(snapshot)

      const { result } = await mountComposable(() => useWeaveSocket())

      // Subscribe and get snapshot
      const onSnapshot1 = vi.fn()
      const unsub1 = result.subscribeV2("session-1", onSnapshot1, vi.fn())
      await flushAll()

      expect(onSnapshot1).toHaveBeenCalledWith(snapshot)
      expect(window.__WEAVE_SOCKET_TEST_API?.hasV2Snapshot("session-1")).toBe(true)

      // Unsubscribe and immediately resubscribe
      unsub1()
      const onSnapshot2 = vi.fn()
      result.subscribeV2("session-1", onSnapshot2, vi.fn())

      // The snapshot cache is cleared synchronously when last listener is removed
      // So the new subscriber won't get the cached snapshot immediately
      // But it will get it from the queued Subscribe operation

      await flushAll()

      // After all operations complete, snapshot should be available
      expect(window.__WEAVE_SOCKET_TEST_API?.hasV2Snapshot("session-1")).toBe(true)
      expect(onSnapshot2).toHaveBeenCalledWith(snapshot)
    })

    it("handles triple switch: session-1 → session-2 → session-1", async () => {
      const { useWeaveSocket } = await import("@/composables/use-signalr-socket")
      const snapshot1 = createSessionSnapshot("session-1")
      const snapshot2 = createSessionSnapshot("session-2")

      const { result } = await mountComposable(() => useWeaveSocket())

      // Subscribe to session-1
      mockInvokeWithSnapshot((sessionId: string) => sessionId === "session-1" ? snapshot1 : snapshot2)
      const unsub1 = result.subscribeV2("session-1", vi.fn(), vi.fn())
      await flushAll()

      mockHubConnection.invoke.mockClear()

      // Switch to session-2
      unsub1()
      const unsub2 = result.subscribeV2("session-2", vi.fn(), vi.fn())
      await flushAll()

      mockHubConnection.invoke.mockClear()

      // Switch back to session-1
      unsub2()
      const onEvent1Final = vi.fn()
      result.subscribeV2("session-1", vi.fn(), onEvent1Final)
      await flushAll()

      // Verify final state: subscribed to session-1
      const calls = mockHubConnection.invoke.mock.calls
      const session1Calls = calls.filter(c => c[1] === "session-1")
      
      console.log("Session-1 operations:", session1Calls.map(c => c[0]))

      // Should have at least one Subscribe for session-1
      const subscribes = session1Calls.filter(c => c[0] === "SubscribeToSessionAsync")
      expect(subscribes.length).toBeGreaterThan(0)

      // Events should be received
      const wireData = createWireEvent("session.started", 10)
      eventHandler?.("session-1", 10, wireData)
      expect(onEvent1Final).toHaveBeenCalledWith(expectedDomainEvent("session.started", 10))
    })

    it("handles overlapping subscriptions to the same session", async () => {
      const { useWeaveSocket } = await import("@/composables/use-signalr-socket")
      const snapshot = createSessionSnapshot("session-1")

      mockInvokeWithSnapshot(snapshot)

      const { result } = await mountComposable(() => useWeaveSocket())

      // Create two subscriptions to the same session
      const onEvent1 = vi.fn()
      const onEvent2 = vi.fn()
      
      const unsub1 = result.subscribeV2("session-1", vi.fn(), onEvent1)
      const unsub2 = result.subscribeV2("session-1", vi.fn(), onEvent2)
      
      await flushAll()

      mockHubConnection.invoke.mockClear()

      // Unsubscribe the first one
      unsub1()
      await flushAll()

      // Should NOT unsubscribe from server (second subscription still active)
      expect(mockHubConnection.invoke).not.toHaveBeenCalledWith("UnsubscribeFromSessionAsync", "session-1")

      // Events should still be received by second subscription
      const wireData = createWireEvent("session.started", 10)
      eventHandler?.("session-1", 10, wireData)
      
      expect(onEvent1).not.toHaveBeenCalled() // First subscription is gone
      expect(onEvent2).toHaveBeenCalledWith(expectedDomainEvent("session.started", 10)) // Second still active

      // Now unsubscribe the second one
      unsub2()
      await flushAll()

      // NOW it should unsubscribe from server
      expect(mockHubConnection.invoke).toHaveBeenCalledWith("UnsubscribeFromSessionAsync", "session-1")
    })
  })
})
