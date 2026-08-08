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
  }
}

function createDomainEvent(type: DomainEvent["type"], eventId?: number): SessionStarted {
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
    mockHubConnection.invoke.mockResolvedValue(undefined)
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

      mockHubConnection.invoke
        .mockResolvedValueOnce(snapshot1)
        .mockResolvedValueOnce(snapshot2)
        .mockResolvedValueOnce(snapshot1)
        .mockResolvedValueOnce(snapshot2)

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

      expect(mockHubConnection.invoke).toHaveBeenCalledTimes(4)
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

      mockHubConnection.invoke.mockResolvedValueOnce(snapshot)

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

      mockHubConnection.invoke.mockResolvedValueOnce(snapshot)

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

      mockHubConnection.invoke.mockRejectedValueOnce(new Error("Subscription failed"))

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

      mockHubConnection.invoke.mockResolvedValueOnce(snapshot)

      const { result } = await mountComposable(() => useWeaveSocket())

      const onSnapshot = vi.fn()
      const onEvent = vi.fn()

      result.subscribeV2("session-1", onSnapshot, onEvent)
      await flushAll()

      const eventData = { type: "session.status", properties: { status: "busy" } }
      eventHandler?.("session-1", 10, eventData)

      expect(onEvent).toHaveBeenCalledWith({ ...eventData, eventId: 10 })
    })

    it("dispatches events without eventId", async () => {
      const { useWeaveSocket } = await import("@/composables/use-signalr-socket")
      const snapshot = createSessionSnapshot("session-1")

      mockHubConnection.invoke.mockResolvedValueOnce(snapshot)

      const { result } = await mountComposable(() => useWeaveSocket())

      const onSnapshot = vi.fn()
      const onEvent = vi.fn()

      result.subscribeV2("session-1", onSnapshot, onEvent)
      await flushAll()

      const eventData = { type: "message.part.delta", properties: { delta: "text" } }
      eventHandler?.("session-1", null, eventData)

      expect(onEvent).toHaveBeenCalledWith(eventData)
    })

    it("dispatches events to multiple subscribers", async () => {
      const { useWeaveSocket } = await import("@/composables/use-signalr-socket")
      const snapshot = createSessionSnapshot("session-1")

      mockHubConnection.invoke.mockResolvedValue(snapshot)

      const { result } = await mountComposable(() => useWeaveSocket())

      const onEvent1 = vi.fn()
      const onEvent2 = vi.fn()

      result.subscribeV2("session-1", vi.fn(), onEvent1)
      result.subscribeV2("session-1", vi.fn(), onEvent2)
      await flushAll()

      const eventData = createDomainEvent("session.started", 10)
      eventHandler?.("session-1", 10, eventData)

      expect(onEvent1).toHaveBeenCalledWith({ ...eventData, eventId: 10 })
      expect(onEvent2).toHaveBeenCalledWith({ ...eventData, eventId: 10 })
    })

    it("does not dispatch events to unrelated topics", async () => {
      const { useWeaveSocket } = await import("@/composables/use-signalr-socket")
      const snapshot1 = createSessionSnapshot("session-1")
      const snapshot2 = createSessionSnapshot("session-2")

      mockHubConnection.invoke.mockResolvedValueOnce(snapshot1).mockResolvedValueOnce(snapshot2)

      const { result } = await mountComposable(() => useWeaveSocket())

      const onEvent1 = vi.fn()
      const onEvent2 = vi.fn()

      result.subscribeV2("session-1", vi.fn(), onEvent1)
      result.subscribeV2("session-2", vi.fn(), onEvent2)
      await flushAll()

      const eventData = createDomainEvent("session.started", 10)
      eventHandler?.("session-1", 10, eventData)

      expect(onEvent1).toHaveBeenCalled()
      expect(onEvent2).not.toHaveBeenCalled()
    })
  })

  describe("v1 compatibility", () => {
    it("dispatches v1 events to v1 subscribers", async () => {
      const { useWeaveSocket } = await import("@/composables/use-signalr-socket")

      const { result } = await mountComposable(() => useWeaveSocket())

      const callback = vi.fn()
      result.subscribe(["session:session-1"], callback)

      const eventData = { type: "session.status", properties: { status: "busy" } }
      eventHandler?.("session:session-1", 10, eventData)

      expect(callback).toHaveBeenCalledWith("session:session-1", eventData)
    })

    it("dispatches to both v1 and v2 subscribers", async () => {
      const { useWeaveSocket } = await import("@/composables/use-signalr-socket")
      const snapshot = createSessionSnapshot("session-1")

      mockHubConnection.invoke.mockResolvedValueOnce(snapshot)

      const { result } = await mountComposable(() => useWeaveSocket())

      const v1Callback = vi.fn()
      const v2Callback = vi.fn()

      result.subscribe(["session-1"], v1Callback)
      result.subscribeV2("session-1", vi.fn(), v2Callback)
      await flushAll()

      const eventData = createDomainEvent("session.started", 10)
      eventHandler?.("session-1", 10, eventData)

      expect(v1Callback).toHaveBeenCalledWith("session-1", eventData)
      expect(v2Callback).toHaveBeenCalledWith({ ...eventData, eventId: 10 })
    })
  })

  describe("unsubscription", () => {
    it("unsubscribes from session when last subscriber leaves", async () => {
      const { useWeaveSocket } = await import("@/composables/use-signalr-socket")
      const snapshot = createSessionSnapshot("session-1")

      mockHubConnection.invoke.mockResolvedValueOnce(snapshot)

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

      mockHubConnection.invoke.mockResolvedValue(snapshot)

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

      mockHubConnection.invoke.mockResolvedValue(snapshot)

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

      mockHubConnection.invoke.mockResolvedValueOnce(snapshot)

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

      mockHubConnection.invoke.mockResolvedValueOnce(snapshot)

      const { result } = await mountComposable(() => useWeaveSocket())

      result.subscribeV2("session-1", vi.fn(), vi.fn())
      await flushAll()

      expect(window.__WEAVE_SOCKET_TEST_API?.v2SnapshotHasText("session-1", "Hello from snapshot")).toBe(true)
      expect(window.__WEAVE_SOCKET_TEST_API?.v2SnapshotHasText("session-1", "not found")).toBe(false)
    })
  })
})
