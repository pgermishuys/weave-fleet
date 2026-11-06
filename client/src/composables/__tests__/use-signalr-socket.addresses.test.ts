import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { targetFor } from "@/lib/machine-target"
import { setActiveMachine, type MachineConnection } from "@/lib/machines"
import { _resetForTesting as resetRoutes, baseUrlOf, kindOf, useAddress } from "@/lib/machine-routes"
import type { SessionSnapshot } from "@/lib/session-snapshot"
import { flushAll, mountComposable } from "./test-utils"

/**
 * A machine with several addresses: the hub connects on the first that answers as the machine, and moves back to a
 * better one only once a connection there is up, never dropping a working one for an address that answers but can't
 * hold a connection (T3 Code's #24516).
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

const { built, refuse } = vi.hoisted(() => ({ built: [] as FakeConnection[], refuse: { start: false, at: "" } }))

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
          if (refuse.start || (refuse.at && connection.url.startsWith(refuse.at))) throw new Error("connection refused")
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

const NAME = "http://studio.tail4f2a.ts.net:2113"
const LAN = "http://192.168.1.42:2113"

const studio: MachineConnection = {
  id: "machine-studio",
  name: "studio",
  baseUrl: NAME,
  token: "studio-token_0123456789-abcdef",
  addedAt: "2026-11-06T09:00:00.000Z",
  addresses: [NAME, LAN].map((url, i) => ({ url, kind: kindOf(url), learned: i > 0 })),
}

/** Which addresses answer the id probe as the machine. */
let answering = new Set<string>()

function hubAt(url: string): FakeConnection[] {
  return built.filter((connection) => connection.url === `${url}/hubs/session-events`)
}

describe("a machine with several addresses", () => {
  beforeEach(async () => {
    built.length = 0
    refuse.start = false
    refuse.at = ""
    localStorage.clear()
    resetRoutes()
    setActiveMachine(null)
    answering = new Set()
    vi.stubGlobal("fetch", vi.fn(async (input: string) => {
      const url = String(input)
      const origin = url.slice(0, url.indexOf("/api/"))
      if (url.endsWith("/api/machine/id") && answering.has(origin)) return Response.json({ id: studio.id, apiVersion: 1 })
      throw new TypeError("Failed to fetch")
    }))
    const { _resetForTesting } = await import("@/composables/use-signalr-socket")
    _resetForTesting()
  })

  afterEach(() => {
    vi.unstubAllGlobals()
  })

  async function listen() {
    const { useWeaveSocket } = await import("@/composables/use-signalr-socket")
    const seen = { snapshots: [] as string[], events: [] as string[] }
    const { result } = await mountComposable(() => useWeaveSocket(targetFor(studio)))
    result.subscribeV2("session:s1", (snapshot: SessionSnapshot) => seen.snapshots.push(snapshot.session.title ?? ""), (event) => seen.events.push(event.type))
    await flushAll()
    return seen
  }

  async function backOnline() {
    window.dispatchEvent(new Event("online"))
    for (let i = 0; i < 20; i++) await flushAll()
  }

  it("connects on the local network when Tailscale doesn't answer", async () => {
    answering.add(LAN)

    await listen()

    expect(built.map((connection) => connection.url)).toEqual([`${LAN}/hubs/session-events`])
    expect(baseUrlOf(studio)).toBe(LAN)
  })

  it("moves back to Tailscale once a connection there is up, then closes the old one", async () => {
    answering.add(LAN)
    const seen = await listen()
    const old = hubAt(LAN)[0]

    answering.add(NAME)
    await backOnline()

    const moved = hubAt(NAME)[0]
    expect(moved).toBeDefined()
    expect(baseUrlOf(studio)).toBe(NAME)
    expect(old.stop).toHaveBeenCalled()
    // Subscribed on the new connection before the old one was closed.
    expect(moved.invoke.mock.invocationCallOrder[0]).toBeLessThan(old.stop.mock.invocationCallOrder[0])
    expect(seen.snapshots.at(-1)).toBe(`From ${NAME}/hubs/session-events`)

    // Only the connection in use is heard.
    old.handlers.get("Event")!("session:s1", 8, { type: "message.updated", properties: {} })
    moved.handlers.get("Event")!("session:s1", 9, { type: "message.created", properties: {} })
    expect(seen.events).toEqual(["message.created"])
  })

  it("stays on a working connection when the better address answers but won't hold one", async () => {
    answering.add(LAN)
    const seen = await listen()
    const old = hubAt(LAN)[0]

    answering.add(NAME)
    refuse.at = NAME
    await backOnline()

    expect(old.stop).not.toHaveBeenCalled()
    expect(baseUrlOf(studio)).toBe(LAN)
    old.handlers.get("Event")!("session:s1", 8, { type: "message.updated", properties: {} })
    expect(seen.events).toEqual(["message.updated"])
  })

  it("doesn't look for a better address while on the best one", async () => {
    answering.add(NAME)
    answering.add(LAN)
    await listen()

    await backOnline()

    expect(built).toHaveLength(1)
    expect(hubAt(NAME)).toHaveLength(1)
  })

  it("starts on the address this tab picked last time", async () => {
    useAddress(studio.id, LAN)
    answering.add(LAN)
    answering.add(NAME)

    await listen()

    expect(hubAt(LAN)).toHaveLength(1)
  })
})
