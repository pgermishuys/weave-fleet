import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { HubConnectionState } from "@microsoft/signalr"
import type { SessionSnapshot } from "@/lib/session-snapshot"
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

function createSessionSnapshot(sessionId: string): SessionSnapshot {
  return {
    session: { id: sessionId, title: "Test Session", status: "idle" },
    messages: [],
    delegations: [],
    activityStatus: "idle",
    lastEventId: 1,
    hasMore: false,
    cursor: null,
  }
}

/**
 * Regression tests for the fire-and-forget unsubscribe race.
 *
 * Bug: navigating A -> B -> A while an agent response was streaming could
 * leave the client detached from the SignalR group. The unsubscribe invoke
 * was fire-and-forget, so a stale unsubscribe could be sent AFTER a
 * re-subscribe for the same topic, removing the connection from the group
 * after the snapshot was delivered.
 *
 * These tests exercise the REAL composable (use-signalr-socket.ts) with a
 * slow-resolving UnsubscribeFromSessionAsync invoke and assert the wire
 * ordering guarantee provided by the per-topic operation queue: a
 * re-subscribe for the same topic must not be sent until the pending
 * unsubscribe has resolved. Removing queueTopicOperation from the
 * implementation makes these tests fail.
 */
describe("useSignalRSocket subscription ordering (race regression)", () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mockHubConnection.state = HubConnectionState.Disconnected
    mockHubConnection.start.mockImplementation(async () => {
      mockHubConnection.state = HubConnectionState.Connected
    })
    mockHubConnection.stop.mockImplementation(async () => {
      mockHubConnection.state = HubConnectionState.Disconnected
    })
    mockHubConnection.on.mockImplementation(() => {})
    mockHubConnection.onreconnected.mockImplementation(() => {})
    mockHubConnection.onclose.mockImplementation(() => {})
  })

  afterEach(async () => {
    const { _resetForTesting } = await import("@/composables/use-signalr-socket")
    _resetForTesting()
  })

  it("does not send a re-subscribe for a topic until a pending unsubscribe resolves", async () => {
    const { useWeaveSocket } = await import("@/composables/use-signalr-socket")

    let resolveUnsubscribe: (() => void) | null = null
    const invokeLog: string[] = []

    mockHubConnection.invoke.mockImplementation((method: string, sessionId: string) => {
      invokeLog.push(method)
      if (method === "UnsubscribeFromSessionAsync") {
        // Simulate a slow in-flight unsubscribe (the race window)
        return new Promise<void>((resolve) => {
          resolveUnsubscribe = resolve
        })
      }
      return Promise.resolve(createSessionSnapshot(sessionId))
    })

    const { result } = await mountComposable(() => useWeaveSocket())

    // Initial subscribe (user opens session A)
    const unsubscribe = result.subscribeV2("session-1", vi.fn(), vi.fn())
    await flushAll()
    expect(invokeLog).toEqual(["SubscribeToSessionAsync"])

    // User navigates away: unsubscribe starts but its invoke stays in flight
    unsubscribe()
    await flushAll()
    expect(invokeLog).toEqual(["SubscribeToSessionAsync", "UnsubscribeFromSessionAsync"])
    expect(resolveUnsubscribe).not.toBeNull()

    // User navigates back while the unsubscribe is still pending
    const onSnapshot = vi.fn()
    result.subscribeV2("session-1", onSnapshot, vi.fn())
    await flushAll()

    // The critical assertion: the re-subscribe must NOT have been sent yet.
    // Without the per-topic queue, a second SubscribeToSessionAsync appears
    // here, and the still-pending unsubscribe can land after it server-side.
    expect(invokeLog).toEqual(["SubscribeToSessionAsync", "UnsubscribeFromSessionAsync"])

    // Once the unsubscribe resolves, the queued re-subscribe goes out
    resolveUnsubscribe!()
    await flushAll()
    expect(invokeLog).toEqual([
      "SubscribeToSessionAsync",
      "UnsubscribeFromSessionAsync",
      "SubscribeToSessionAsync",
    ])

    // And the fresh snapshot is delivered to the new subscriber
    expect(onSnapshot).toHaveBeenCalledWith(createSessionSnapshot("session-1"))
  })

  it("keeps strict per-topic ordering across repeated rapid navigation cycles", async () => {
    const { useWeaveSocket } = await import("@/composables/use-signalr-socket")

    const pendingUnsubscribes: Array<() => void> = []
    const invokeLog: string[] = []

    mockHubConnection.invoke.mockImplementation((method: string, sessionId: string) => {
      invokeLog.push(method)
      if (method === "UnsubscribeFromSessionAsync") {
        return new Promise<void>((resolve) => {
          pendingUnsubscribes.push(resolve)
        })
      }
      return Promise.resolve(createSessionSnapshot(sessionId))
    })

    const { result } = await mountComposable(() => useWeaveSocket())

    // Simulate 3 rapid A -> away -> A cycles without waiting for unsubscribes
    let unsubscribe = result.subscribeV2("session-1", vi.fn(), vi.fn())
    await flushAll()

    for (let i = 0; i < 3; i++) {
      unsubscribe()
      unsubscribe = result.subscribeV2("session-1", vi.fn(), vi.fn())
      await flushAll()
    }

    // Only the first subscribe and the first (still pending) unsubscribe
    // should have hit the wire; everything else is queued behind it.
    expect(invokeLog).toEqual(["SubscribeToSessionAsync", "UnsubscribeFromSessionAsync"])

    // Drain the queue by resolving unsubscribes as they are issued
    while (pendingUnsubscribes.length > 0) {
      pendingUnsubscribes.shift()!()
      await flushAll()
    }

    // Strict alternation, ending on a subscribe (user is on session A)
    expect(invokeLog).toEqual([
      "SubscribeToSessionAsync",
      "UnsubscribeFromSessionAsync",
      "SubscribeToSessionAsync",
      "UnsubscribeFromSessionAsync",
      "SubscribeToSessionAsync",
      "UnsubscribeFromSessionAsync",
      "SubscribeToSessionAsync",
    ])
  })
})
