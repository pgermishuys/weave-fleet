/**
 * V2 Session-Switching Race Condition Tests (RED phase — TDD)
 *
 * These tests assert the CORRECT behavior we want after switching sessions rapidly.
 * They should FAIL against the current implementation, proving the bug exists.
 *
 * ## Desired Behavior
 *
 * When navigating to a session:
 * - You see the current truth (reply in progress or completed reply)
 * - The subscriber receives exactly ONE snapshot — the authoritative fresh one
 * - No stale snapshots from prior queued operations are dispatched
 * - No events are lost during the transition
 *
 * ## What Currently Goes Wrong
 *
 * When rapidly switching Session 1 → Session 2 → Session 1, the per-topic operation queue
 * ends up as [Subscribe, Unsubscribe, Subscribe]. The first Subscribe's snapshot gets
 * dispatched to the NEW subscriber (stale data), and there's a server-side gap where
 * events are lost.
 */

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { HubConnectionState } from "@microsoft/signalr"
import type { SessionSnapshot } from "@/lib/session-snapshot"
import type { DomainEvent } from "@/lib/domain-events"
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
// eslint-disable-next-line @typescript-eslint/no-unused-vars
let _reconnectedHandler: (() => void) | null = null
// eslint-disable-next-line @typescript-eslint/no-unused-vars
let _closeHandler: (() => void) | null = null

function createDeferred<T>() {
  let resolve!: (value: T) => void
  let reject!: (reason?: unknown) => void
  const promise = new Promise<T>((res, rej) => {
    resolve = res
    reject = rej
  })
  return { promise, resolve, reject }
}

function createSessionSnapshot(sessionId: string, messageText: string = "Hello from snapshot"): SessionSnapshot {
  return {
    session: {
      id: sessionId,
      title: "Test Session",
      status: "idle",
    },
    messages: messageText
      ? [
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
            parts: [{ id: "part-1", sessionID: sessionId, messageID: "msg-1", type: "text", text: messageText }],
          },
        ]
      : [],
    delegations: [],
    activityStatus: "idle",
    lastEventId: 5,
    hasMore: false,
    cursor: null,
  }
}

describe("useSignalRSocket V2 — desired behavior after rapid session switching", () => {
  beforeEach(() => {
    vi.clearAllMocks()
    eventHandler = null
    _reconnectedHandler = null
    _closeHandler = null

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
      _reconnectedHandler = handler
    })
    mockHubConnection.onclose.mockImplementation((handler: () => void) => {
      _closeHandler = handler
    })
  })

  afterEach(async () => {
    const { _resetForTesting } = await import("@/composables/use-signalr-socket")
    _resetForTesting()
  })

  it("new subscriber receives only the fresh snapshot, not a stale one from a prior queued subscribe", async () => {
    const { useWeaveSocket } = await import("@/composables/use-signalr-socket")

    const subscribe1Deferred = createDeferred<SessionSnapshot>()
    const unsubscribe1Deferred = createDeferred<void>()
    const subscribe2Deferred = createDeferred<SessionSnapshot>()

    let subscribeCallCount = 0
    mockHubConnection.invoke.mockImplementation((method: string) => {
      if (method === "SubscribeToSessionAsync") {
        subscribeCallCount++
        if (subscribeCallCount === 1) return subscribe1Deferred.promise
        if (subscribeCallCount === 2) return subscribe2Deferred.promise
      } else if (method === "UnsubscribeFromSessionAsync") {
        return unsubscribe1Deferred.promise
      }
      return Promise.resolve()
    })

    const { result } = await mountComposable(() => useWeaveSocket())

    // T0: Subscribe to session-1
    const onSnapshot_A = vi.fn()
    const unsubscribe_A = result.subscribeV2("session-1", onSnapshot_A, vi.fn())
    await flushAll()

    // T1: Switch away — unsubscribe from session-1
    unsubscribe_A()
    await flushAll()

    // T2: Switch back — subscribe to session-1 again with NEW callbacks
    const onSnapshot_C = vi.fn()
    result.subscribeV2("session-1", onSnapshot_C, vi.fn())
    await flushAll()

    // Resolve first subscribe (stale — e.g., agent hadn't started yet)
    const staleSnapshot = createSessionSnapshot("session-1", "")
    subscribe1Deferred.resolve(staleSnapshot)
    await flushAll()

    // Resolve unsubscribe
    unsubscribe1Deferred.resolve()
    await flushAll()

    // Resolve second subscribe (fresh — agent has replied)
    const freshSnapshot = createSessionSnapshot("session-1", "Agent response")
    subscribe2Deferred.resolve(freshSnapshot)
    await flushAll()

    // CORRECT BEHAVIOR: onSnapshot_C should be called exactly ONCE with the fresh snapshot.
    // The stale snapshot from the first (now-irrelevant) subscribe should NOT reach the new subscriber.
    expect(onSnapshot_C).toHaveBeenCalledTimes(1)
    expect(onSnapshot_C).toHaveBeenCalledWith(freshSnapshot)
  })

  it("after rapid unsub/resub, the last server operation is Subscribe (not Unsubscribe)", async () => {
    const { useWeaveSocket } = await import("@/composables/use-signalr-socket")

    const invokeCalls: Array<{ method: string; sessionId: string }> = []
    mockHubConnection.invoke.mockImplementation((method: string, sessionId: string) => {
      invokeCalls.push({ method, sessionId })
      return Promise.resolve(createSessionSnapshot(sessionId, "snap"))
    })

    const { result } = await mountComposable(() => useWeaveSocket())

    // Subscribe to session-1
    const unsub = result.subscribeV2("session-1", vi.fn(), vi.fn())
    await flushAll()

    // Rapidly unsubscribe + resubscribe (simulating nav away and back)
    unsub()
    result.subscribeV2("session-1", vi.fn(), vi.fn())
    await flushAll()

    const session1Calls = invokeCalls.filter((c) => c.sessionId === "session-1")

    // CORRECT BEHAVIOR: The Unsubscribe should be elided or the final operation should be Subscribe.
    // There should be no gap where the server thinks we're unsubscribed.
    // Ideally: [Subscribe] or [Subscribe, Subscribe] — NOT [Subscribe, Unsubscribe, Subscribe]
    const lastCall = session1Calls[session1Calls.length - 1]
    expect(lastCall.method).toBe("SubscribeToSessionAsync")

    // And critically, there should be NO Unsubscribe call at all since we immediately resubscribed
    const hasUnsubscribe = session1Calls.some((c) => c.method === "UnsubscribeFromSessionAsync")
    expect(hasUnsubscribe).toBe(false)
  })

  it("events arriving after re-subscribe are not wiped by a late-arriving stale snapshot", async () => {
    const { useWeaveSocket } = await import("@/composables/use-signalr-socket")

    const subscribe1Deferred = createDeferred<SessionSnapshot>()
    const subscribe2Deferred = createDeferred<SessionSnapshot>()

    let subscribeCallCount = 0
    mockHubConnection.invoke.mockImplementation((method: string) => {
      if (method === "SubscribeToSessionAsync") {
        subscribeCallCount++
        if (subscribeCallCount === 1) return subscribe1Deferred.promise
        if (subscribeCallCount === 2) return subscribe2Deferred.promise
      }
      return Promise.resolve()
    })

    const { result } = await mountComposable(() => useWeaveSocket())

    // Subscribe to session-1
    const onSnapshot_A = vi.fn()
    const unsubscribe_A = result.subscribeV2("session-1", onSnapshot_A, vi.fn())
    await flushAll()

    // Unsubscribe and immediately re-subscribe with NEW callbacks
    unsubscribe_A()
    const onSnapshot_C = vi.fn()
    const onEvent_C = vi.fn()
    result.subscribeV2("session-1", onSnapshot_C, onEvent_C)
    await flushAll()

    // Resolve second subscribe FIRST (fresh snapshot — this is what we want)
    // In a correct implementation, the first subscribe's result is discarded
    const freshSnapshot = createSessionSnapshot("session-1", "Agent response")
    subscribe2Deferred.resolve(freshSnapshot)
    await flushAll()

    // Now events arrive (agent still streaming)
    const streamEvent: DomainEvent = {
      type: "message.part.delta",
      payload: { sessionId: "session-1", messageId: "msg-2", partId: "part-2", delta: "more text" },
      eventId: 11,
    }
    eventHandler?.("session-1", 11, streamEvent)
    await flushAll()

    expect(onEvent_C).toHaveBeenCalledWith({ ...streamEvent, eventId: 11 })

    // Now the STALE first subscribe resolves (late arrival)
    const staleSnapshot = createSessionSnapshot("session-1", "")
    staleSnapshot.messages = []
    subscribe1Deferred.resolve(staleSnapshot)
    await flushAll()

    // CORRECT BEHAVIOR: The stale snapshot should NOT trigger another onSnapshot call.
    // The subscriber already has the fresh snapshot + live events.
    // Dispatching the stale snapshot would wipe the state.
    expect(onSnapshot_C).toHaveBeenCalledTimes(1)
    expect(onSnapshot_C).toHaveBeenCalledWith(freshSnapshot)
  })

  it("triple switch (S1→S2→S1): final subscriber sees only the fresh S1 snapshot", async () => {
    const { useWeaveSocket } = await import("@/composables/use-signalr-socket")

    const deferreds = {
      subscribeS1_1: createDeferred<SessionSnapshot>(),
      subscribeS2: createDeferred<SessionSnapshot>(),
      subscribeS1_2: createDeferred<SessionSnapshot>(),
    }

    let subscribeS1Count = 0
    // eslint-disable-next-line @typescript-eslint/no-unused-vars
    let _subscribeS2Count = 0
    mockHubConnection.invoke.mockImplementation((method: string, sessionId: string) => {
      if (method === "SubscribeToSessionAsync") {
        if (sessionId === "session-1") {
          subscribeS1Count++
          return subscribeS1Count === 1 ? deferreds.subscribeS1_1.promise : deferreds.subscribeS1_2.promise
        }
        if (sessionId === "session-2") {
          _subscribeS2Count++
          return deferreds.subscribeS2.promise
        }
      }
      return Promise.resolve()
    })

    const { result } = await mountComposable(() => useWeaveSocket())

    // S1 initial subscribe
    const unsub1 = result.subscribeV2("session-1", vi.fn(), vi.fn())
    await flushAll()

    // Switch to S2
    unsub1()
    const unsub2 = result.subscribeV2("session-2", vi.fn(), vi.fn())
    await flushAll()

    // Switch back to S1
    unsub2()
    const onSnapshot_final = vi.fn()
    const onEvent_final = vi.fn()
    result.subscribeV2("session-1", onSnapshot_final, onEvent_final)
    await flushAll()

    // Resolve all in order (simulating server processing queue)
    deferreds.subscribeS1_1.resolve(createSessionSnapshot("session-1", "stale s1"))
    await flushAll()
    deferreds.subscribeS2.resolve(createSessionSnapshot("session-2", "s2 content"))
    await flushAll()
    deferreds.subscribeS1_2.resolve(createSessionSnapshot("session-1", "fresh s1 — agent replied"))
    await flushAll()

    // CORRECT BEHAVIOR: The final S1 subscriber sees exactly one snapshot — the fresh one
    expect(onSnapshot_final).toHaveBeenCalledTimes(1)
    expect(onSnapshot_final).toHaveBeenCalledWith(
      expect.objectContaining({
        messages: expect.arrayContaining([
          expect.objectContaining({
            parts: expect.arrayContaining([
              expect.objectContaining({ text: "fresh s1 — agent replied" }),
            ]),
          }),
        ]),
      }),
    )
  })

  it("no events are dispatched to a subscriber that has been unsubscribed", async () => {
    const { useWeaveSocket } = await import("@/composables/use-signalr-socket")

    mockHubConnection.invoke.mockResolvedValue(createSessionSnapshot("session-1", "snap"))

    const { result } = await mountComposable(() => useWeaveSocket())

    const onEvent_old = vi.fn()
    const unsub = result.subscribeV2("session-1", vi.fn(), onEvent_old)
    await flushAll()

    // Unsubscribe
    unsub()
    await flushAll()

    // Events arrive for the topic after unsubscribe
    const event: DomainEvent = {
      type: "session.status",
      payload: { status: "busy" },
      eventId: 10,
    }
    eventHandler?.("session-1", 10, event)

    // CORRECT BEHAVIOR: The old callback should NOT be called after unsubscribe
    expect(onEvent_old).not.toHaveBeenCalled()
  })
})
