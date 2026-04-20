import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { createPinia, setActivePinia } from "pinia"
import { nextTick } from "vue"
import { dispatchSessionUpsert } from "@/lib/session-sync"
import { sessionCache } from "@/lib/session-cache"
import { sortAccumulatedMessagesChronologically } from "@/lib/pagination-utils"
import { useSessionsStore } from "@/stores/sessions"
import { flushAll, mountComposable } from "./test-utils"

const {
  apiFetchMock,
  subscribeMock,
  isWeaveSocketConnectedMock,
  onReconnectMock,
  onDisconnectMock,
  clearPendingPromptsMock,
  clearSentPromptsMock,
  loadInitialMessagesMock,
  loadOlderMessagesMock,
  resetPaginationMock,
  snapshotPaginationMock,
  hydratePaginationMock,
} = vi.hoisted(() => ({
  apiFetchMock: vi.fn(),
  subscribeMock: vi.fn(),
  isWeaveSocketConnectedMock: vi.fn(() => true),
  onReconnectMock: vi.fn(),
  onDisconnectMock: vi.fn(),
  clearPendingPromptsMock: vi.fn(),
  clearSentPromptsMock: vi.fn(),
  loadInitialMessagesMock: vi.fn(),
  loadOlderMessagesMock: vi.fn(),
  resetPaginationMock: vi.fn(),
  snapshotPaginationMock: vi.fn(() => ({ hasMore: false, oldestMessageId: null, totalCount: null })),
  hydratePaginationMock: vi.fn(),
}))

let socketCallback: ((topic: string, data: unknown) => void) | null = null
let reconnectCallback: (() => void) | null = null
let disconnectCallback: (() => void) | null = null

vi.mock("@/lib/api-client", () => ({
  apiFetch: apiFetchMock,
}))

vi.mock("@/composables/use-weave-socket", () => ({
  useWeaveSocket: () => ({
    subscribe: subscribeMock,
  }),
  isWeaveSocketConnected: isWeaveSocketConnectedMock,
  onReconnect: onReconnectMock,
  onDisconnect: onDisconnectMock,
}))

vi.mock("@/composables/use-send-prompt", () => ({
  clearPendingPrompts: clearPendingPromptsMock,
  clearSentPrompts: clearSentPromptsMock,
}))

vi.mock("@/composables/use-message-pagination", async () => {
  const { shallowRef, readonly } = await import("vue")
  const hasMore = shallowRef(false)
  const isLoadingOlder = shallowRef(false)
  const totalCount = shallowRef<number | null>(null)
  const loadError = shallowRef<string | null>(null)

  return {
    useMessagePagination: () => ({
      hasMore: readonly(hasMore),
      isLoadingOlder: readonly(isLoadingOlder),
      totalCount: readonly(totalCount),
      loadError: readonly(loadError),
      loadInitialMessages: loadInitialMessagesMock,
      loadOlderMessages: loadOlderMessagesMock,
      resetPagination: resetPaginationMock,
      snapshotPagination: snapshotPaginationMock,
      hydratePagination: hydratePaginationMock,
    }),
  }
})

function createJsonResponse<T>(body: T, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  })
}

function createSessionListItem() {
  return {
    instanceId: "instance-1",
    workspaceId: "workspace-1",
    workspaceDirectory: "/tmp/project",
    workspaceDisplayName: "project",
    isolationStrategy: "existing" as const,
    sessionStatus: "active" as const,
    session: {
      id: "session-1",
      title: "Session 1",
      time: {
        created: 1,
        updated: 2,
      },
    },
    instanceStatus: "running" as const,
    parentSessionId: null,
    sourceDirectory: "/tmp/project",
    branch: "main",
    activityStatus: "busy" as const,
    lifecycleStatus: "running" as const,
    retentionStatus: "active" as const,
    archivedAt: null,
    typedInstanceStatus: "running" as const,
    isHidden: false,
    projectId: "project-1",
    projectName: "API",
    totalTokens: 10,
    totalCost: 1,
  }
}

describe("useSessionEvents", () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    sessionCache.clear()
    socketCallback = null
    reconnectCallback = null
    disconnectCallback = null

    apiFetchMock.mockReset()
    subscribeMock.mockReset()
    isWeaveSocketConnectedMock.mockReset()
    onReconnectMock.mockReset()
    onDisconnectMock.mockReset()
    clearPendingPromptsMock.mockReset()
    clearSentPromptsMock.mockReset()
    loadInitialMessagesMock.mockReset()
    loadOlderMessagesMock.mockReset()
    resetPaginationMock.mockReset()
    snapshotPaginationMock.mockClear()
    hydratePaginationMock.mockReset()

    loadInitialMessagesMock.mockResolvedValue([])
    loadOlderMessagesMock.mockResolvedValue([])
    isWeaveSocketConnectedMock.mockReturnValue(true)
    subscribeMock.mockImplementation((_topics: string[], callback: (topic: string, data: unknown) => void) => {
      socketCallback = callback
      return () => {
        socketCallback = null
      }
    })
    onReconnectMock.mockImplementation((callback: () => void) => {
      reconnectCallback = callback
      return () => {
        reconnectCallback = null
      }
    })
    onDisconnectMock.mockImplementation((callback: () => void) => {
      disconnectCallback = callback
      return () => {
        disconnectCallback = null
      }
    })
    apiFetchMock.mockImplementation((url: string) => {
      if (url.includes("/delegations")) {
        return Promise.resolve(createJsonResponse([]))
      }

      if (url.includes("/committed-events")) {
        return Promise.resolve(createJsonResponse({ events: [] }))
      }

      throw new Error(`Unexpected apiFetch call: ${url}`)
    })
  })

  afterEach(() => {
    sessionCache.clear()
  })

  it("does not fetch session status during initial load", async () => {
    const { useSessionEvents } = await import("@/composables/use-session-events")

    const { result } = await mountComposable(() => useSessionEvents("session-1", "instance-1"))
    const requestedUrls = apiFetchMock.mock.calls.map(([url]) => url)

    expect(loadInitialMessagesMock).toHaveBeenCalledWith("session-1", "instance-1", expect.any(AbortSignal))
    expect(apiFetchMock).toHaveBeenCalledWith("/api/sessions/session-1/delegations", { signal: expect.any(AbortSignal) })
    expect(requestedUrls).not.toContain("/api/sessions/session-1/status?instanceId=instance-1")
    expect(result.sessionStatus.value).toBe("idle")
  })

  it("does not fetch session status from cache refresh, reconnect callback, or manual reconnect", async () => {
    sessionCache.set("session-1", "instance-1", {
      messages: [],
      delegations: [],
      scrollPosition: 0,
      scrollHeight: 0,
      sessionStatus: "busy",
      lastSequenceNumber: 7,
      pagination: { hasMore: false, oldestMessageId: null, totalCount: null },
      timestamp: Date.now(),
    })

    const { useSessionEvents } = await import("@/composables/use-session-events")
    const { result } = await mountComposable(() => useSessionEvents("session-1", "instance-1"))

    expect(hydratePaginationMock).toHaveBeenCalled()
    expect(result.sessionStatus.value).toBe("busy")

    await reconnectCallback?.()
    await flushAll()

    await reconnectCallback?.()
    await flushAll()

    result.reconnect()
    await flushAll()

    expect(apiFetchMock.mock.calls.map(([url]) => url)).toEqual([
      "/api/sessions/session-1/committed-events?afterSequenceNumber=7",
      "/api/sessions/session-1/delegations",
      "/api/sessions/session-1/committed-events?afterSequenceNumber=7",
      "/api/sessions/session-1/delegations",
      "/api/sessions/session-1/committed-events?afterSequenceNumber=7",
      "/api/sessions/session-1/delegations",
      "/api/sessions/session-1/committed-events?afterSequenceNumber=7",
      "/api/sessions/session-1/delegations",
    ])
    expect(apiFetchMock.mock.calls.map(([url]) => url)).not.toContain("/api/sessions/session-1/status?instanceId=instance-1")
  })

  it("keeps websocket and session-sync status updates working without HTTP fallback", async () => {
    const store = useSessionsStore()
    store.setSessions([createSessionListItem()])

    const { useSessionEvents } = await import("@/composables/use-session-events")
    const { result } = await mountComposable(() => useSessionEvents("session-1", "instance-1"))

    socketCallback?.("session:session-1", {
      type: "session.status",
      properties: { status: "busy" },
    })
    await nextTick()

    expect(result.sessionStatus.value).toBe("busy")
    expect(store.sessions[0]?.activityStatus).toBe("busy")
    expect(store.sessions[0]?.sessionStatus).toBe("active")

    dispatchSessionUpsert({
      ...createSessionListItem(),
      activityStatus: "idle",
      sessionStatus: "idle",
    })
    await nextTick()

    expect(result.sessionStatus.value).toBe("idle")
    expect(store.sessions[0]?.sessionStatus).toBe("active")
  })

  it("keeps replayed older messages in chronological order", async () => {
    loadInitialMessagesMock.mockResolvedValue([
      {
        messageId: "msg-new",
        sessionId: "session-1",
        role: "assistant",
        parts: [{ partId: "part-new", type: "text", text: "newer" }],
        createdAt: 2_000,
        agent: "Loom (Main Orchestrator)",
      },
    ])

    const { useSessionEvents } = await import("@/composables/use-session-events")
    const { result } = await mountComposable(() => useSessionEvents("session-1", "instance-1"))

    await flushAll()

    socketCallback?.("session:session-1", {
      type: "message.updated",
      properties: {
        info: {
          id: "msg-old",
          role: "assistant",
          sessionID: "session-1",
          agent: "Loom (Main Orchestrator)",
          time: { created: 1_000 },
        },
      },
    })

    socketCallback?.("session:session-1", {
      type: "message.part.updated",
      properties: {
        part: {
          id: "part-old",
          messageID: "msg-old",
          sessionID: "session-1",
          type: "text",
          text: "older",
        },
      },
    })

    await nextTick()

    expect(result.messages.value.map((message) => message.messageId)).toEqual(["msg-old", "msg-new"])
    expect(result.messages.value.map((message) => message.parts[0]?.type === "text" ? message.parts[0].text : "")).toEqual(["older", "newer"])
  })

  it("sorts loaded history pages by authoritative message timestamp", () => {
    const ordered = sortAccumulatedMessagesChronologically([
      {
        messageId: "msg-later-inserted-but-earlier-authored",
        sessionId: "session-1",
        role: "assistant",
        parts: [{ partId: "part-1", type: "text", text: "older authored" }],
        createdAt: 1_000,
      },
      {
        messageId: "msg-earlier-inserted-but-later-authored",
        sessionId: "session-1",
        role: "assistant",
        parts: [{ partId: "part-2", type: "text", text: "later authored" }],
        createdAt: 2_000,
      },
    ])

    expect(ordered.map((message) => message.messageId)).toEqual([
      "msg-later-inserted-but-earlier-authored",
      "msg-earlier-inserted-but-later-authored",
    ])
  })
})
