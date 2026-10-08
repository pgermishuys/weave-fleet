import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { liveTarget, targetFor } from "@/lib/machine-target"
import { setActiveMachine, type MachineConnection } from "@/lib/machines"
import type { SessionSnapshot } from "@/lib/session-snapshot"
import { flushAll, mountComposable } from "./test-utils"

/**
 * One hub connection per machine: each opens on its own address with its own key, holds its own topics, and hears
 * only its own events. With one machine, everything is as it was (the other socket tests).
 */

interface FakeConnection {
  url: string
  options: { accessTokenFactory?: () => string; withCredentials?: boolean }
  state: number
  invoke: ReturnType<typeof vi.fn>
  stop: ReturnType<typeof vi.fn>
  handlers: Map<string, (...args: unknown[]) => void>
  reconnected: (() => Promise<void> | void) | null
  closed: (() => void) | null
}

const { built } = vi.hoisted(() => ({ built: [] as FakeConnection[] }))

vi.mock("@microsoft/signalr", () => {
  class MockHubConnectionBuilder {
    private url = ""
    private options = {}
    withUrl(url: string, options: object) {
      this.url = url
      this.options = options
      return this
    }
    withAutomaticReconnect() {
      return this
    }
    build() {
      const connection: FakeConnection & Record<string, unknown> = {
        url: this.url,
        options: this.options,
        state: 0,
        invoke: vi.fn(async (method: string, sessionId?: string) =>
          method === "SubscribeToSessionAsync" ? snapshotOf(sessionId ?? "", connection.url) : undefined),
        stop: vi.fn(async () => {
          connection.state = 0
        }),
        handlers: new Map(),
        reconnected: null,
        closed: null,
        start: vi.fn(async () => {
          connection.state = 2
        }),
        on: (name: string, handler: (...args: unknown[]) => void) => connection.handlers.set(name, handler),
        onreconnecting: () => {},
        onreconnected: (handler: () => Promise<void>) => {
          connection.reconnected = handler
        },
        onclose: (handler: () => void) => {
          connection.closed = handler
        },
      }
      built.push(connection)
      return connection
    }
  }

  return {
    HubConnectionBuilder: MockHubConnectionBuilder,
    HubConnectionState: { Disconnected: 0, Connecting: 1, Connected: 2, Disconnecting: 3, Reconnecting: 4 },
  }
})

function snapshotOf(sessionId: string, from: string): SessionSnapshot {
  return {
    session: { id: sessionId, title: `From ${from}`, status: "idle" },
    messages: [],
    delegations: [],
    activityStatus: "idle",
    lastEventId: null,
    hasMore: false,
    cursor: null,
    isPartial: false,
  } as unknown as SessionSnapshot
}

const falcon: MachineConnection = {
  id: "f0a1c2d3e4f5a6b7c8d9e0f1a2b3c4d5",
  name: "falcon",
  baseUrl: "http://100.64.90.72:2113",
  token: "falcon-token_0123456789-abcdef",
  addedAt: "2026-09-26T00:00:00.000Z",
}

function connectionTo(url: string): FakeConnection {
  const connection = built.find((candidate) => candidate.url === url)
  if (!connection) throw new Error(`No connection to ${url}; built: ${built.map((candidate) => candidate.url).join(", ")}`)
  return connection
}

const HOME_HUB = "/hubs/session-events"
const FALCON_HUB = `${falcon.baseUrl}/hubs/session-events`

describe("one hub connection per machine", () => {
  beforeEach(async () => {
    built.length = 0
    setActiveMachine(null)
    const { _resetForTesting } = await import("@/composables/use-signalr-socket")
    _resetForTesting()
  })

  afterEach(() => {
    setActiveMachine(null)
  })

  it("connects to each machine on its own address: home with its cookie, another with its token", async () => {
    const { useWeaveSocket } = await import("@/composables/use-signalr-socket")

    await mountComposable(() => useWeaveSocket(liveTarget()))
    await mountComposable(() => useWeaveSocket(targetFor(falcon)))
    await flushAll()

    expect(built.map((connection) => connection.url).sort()).toEqual([FALCON_HUB, HOME_HUB].sort())
    expect(connectionTo(HOME_HUB).options).toEqual({})
    expect(connectionTo(FALCON_HUB).options.withCredentials).toBe(false)
    expect(connectionTo(FALCON_HUB).options.accessTokenFactory?.()).toBe(falcon.token)
  })

  it("shares one connection between everything listening to a machine", async () => {
    const { useWeaveSocket } = await import("@/composables/use-signalr-socket")

    await mountComposable(() => useWeaveSocket(targetFor(falcon)))
    await mountComposable(() => useWeaveSocket(targetFor(falcon)))
    await flushAll()

    expect(built).toHaveLength(1)
  })

  it("subscribes a session on its machine, and gives it that machine's snapshot and events only", async () => {
    const { useWeaveSocket } = await import("@/composables/use-signalr-socket")
    const atHome = { snapshots: [] as SessionSnapshot[], events: [] as string[] }
    const onFalcon = { snapshots: [] as SessionSnapshot[], events: [] as string[] }

    const { result: home } = await mountComposable(() => useWeaveSocket(liveTarget()))
    const { result: other } = await mountComposable(() => useWeaveSocket(targetFor(falcon)))
    await flushAll()
    // The same id on both: only the machine tells them apart.
    home.subscribeV2("session:s1", (snapshot) => atHome.snapshots.push(snapshot), (event) => atHome.events.push(event.type))
    other.subscribeV2("session:s1", (snapshot) => onFalcon.snapshots.push(snapshot), (event) => onFalcon.events.push(event.type))
    await flushAll()

    connectionTo(FALCON_HUB).handlers.get("Event")!("session:s1", 7, { type: "message.updated", properties: {} })

    expect(atHome.snapshots.map((snapshot) => snapshot.session.title)).toEqual([`From ${HOME_HUB}`])
    expect(onFalcon.snapshots.map((snapshot) => snapshot.session.title)).toEqual([`From ${FALCON_HUB}`])
    expect(onFalcon.events).toEqual(["message.updated"])
    expect(atHome.events).toEqual([])
  })

  it("hears a machine's sessions topic only from that machine", async () => {
    const { onGlobalEvent, useWeaveSocket } = await import("@/composables/use-signalr-socket")
    const atHome: string[] = []
    const onFalcon: string[] = []

    await mountComposable(() => useWeaveSocket(liveTarget()))
    await mountComposable(() => useWeaveSocket(targetFor(falcon)))
    onGlobalEvent(liveTarget(), "sessions", (event) => atHome.push(event.type))
    onGlobalEvent(targetFor(falcon), "sessions", (event) => onFalcon.push(event.type))
    await flushAll()

    connectionTo(FALCON_HUB).handlers.get("Event")!("sessions", null, { type: "activity_status", properties: {} })
    connectionTo(HOME_HUB).handlers.get("Event")!("sessions", null, { type: "session.created", properties: {} })

    expect(onFalcon).toEqual(["activity_status"])
    expect(atHome).toEqual(["session.created"])
  })

  it("tells a machine's reconnect listeners when that machine reconnects, and no one else's", async () => {
    const { onReconnect, useWeaveSocket } = await import("@/composables/use-signalr-socket")
    const atHome = vi.fn()
    const onFalcon = vi.fn()

    await mountComposable(() => useWeaveSocket(liveTarget()))
    await mountComposable(() => useWeaveSocket(targetFor(falcon)))
    onReconnect(liveTarget(), atHome)
    onReconnect(targetFor(falcon), onFalcon)
    await flushAll()

    await connectionTo(FALCON_HUB).reconnected!()

    expect(onFalcon).toHaveBeenCalledOnce()
    expect(atHome).not.toHaveBeenCalled()
  })

  it("closes a machine's connection with its last listener, leaving the others open", async () => {
    const { isWeaveSocketConnected, useWeaveSocket } = await import("@/composables/use-signalr-socket")

    await mountComposable(() => useWeaveSocket(liveTarget()))
    const { wrapper } = await mountComposable(() => useWeaveSocket(targetFor(falcon)))
    await flushAll()
    wrapper.unmount()
    await flushAll()

    expect(connectionTo(FALCON_HUB).stop).toHaveBeenCalled()
    expect(isWeaveSocketConnected(targetFor(falcon))).toBe(false)
    expect(connectionTo(HOME_HUB).stop).not.toHaveBeenCalled()
    expect(isWeaveSocketConnected(liveTarget())).toBe(true)
  })

  it("asks a session's machine for its older messages and its focus", async () => {
    const { loadSessionHistory, setSessionFocus, useWeaveSocket } = await import("@/composables/use-signalr-socket")

    await mountComposable(() => useWeaveSocket(liveTarget()))
    await mountComposable(() => useWeaveSocket(targetFor(falcon)))
    await flushAll()
    await loadSessionHistory(targetFor(falcon), "s1", "c1")
    setSessionFocus(targetFor(falcon), "s1", true)

    expect(connectionTo(FALCON_HUB).invoke).toHaveBeenCalledWith("LoadHistoryAsync", "s1", "c1")
    expect(connectionTo(FALCON_HUB).invoke).toHaveBeenCalledWith("SetSessionFocusAsync", "s1", true)
    expect(connectionTo(HOME_HUB).invoke.mock.calls.map(([method]) => method)).not.toContain("LoadHistoryAsync")
    expect(connectionTo(HOME_HUB).invoke.mock.calls.map(([method]) => method)).not.toContain("SetSessionFocusAsync")
  })

  it("answers the test API for the live machine", async () => {
    const { useWeaveSocket } = await import("@/composables/use-signalr-socket")

    const { result: other } = await mountComposable(() => useWeaveSocket(targetFor(falcon)))
    await flushAll()
    other.subscribeV2("session:s1", () => {}, () => {})
    await flushAll()

    expect(window.__WEAVE_SOCKET_TEST_API?.hasOpenSocket()).toBe(false)
    expect(window.__WEAVE_SOCKET_TEST_API?.hasV2Subscriptions()).toBe(false)
  })
})
