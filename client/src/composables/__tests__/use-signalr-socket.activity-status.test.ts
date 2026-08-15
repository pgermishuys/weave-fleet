import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { HubConnectionState } from "@microsoft/signalr"
import { createPinia, setActivePinia } from "pinia"
import { flushAll, mountComposable } from "./test-utils"
import type { SessionActionCapabilities } from "@/lib/types"

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

// Helper to create complete capabilities object
function createCapabilities(overrides: Partial<SessionActionCapabilities> = {}): SessionActionCapabilities {
  return {
    canPrompt: false,
    canStop: false,
    canResume: false,
    canRestart: false,
    canAbort: false,
    canArchive: false,
    canUnarchive: false,
    canFork: false,
    canDelete: false,
    promptDisabledReason: null,
    stopDisabledReason: null,
    resumeDisabledReason: null,
    restartDisabledReason: null,
    abortDisabledReason: null,
    archiveDisabledReason: null,
    unarchiveDisabledReason: null,
    forkDisabledReason: null,
    deleteDisabledReason: null,
    ...overrides,
  }
}

describe("activity_status event handling", () => {
  beforeEach(() => {
    // Reset all mocks
    vi.clearAllMocks()
    eventHandler = null
    reconnectedHandler = null

    // Setup Pinia
    setActivePinia(createPinia())

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
  })

  afterEach(async () => {
    const { _resetForTesting } = await import("@/composables/use-signalr-socket")
    _resetForTesting()
  })

  it("subscribes to global sessions topic on connect", async () => {
    const { useWeaveSocket } = await import("@/composables/use-signalr-socket")

    await mountComposable(() => useWeaveSocket())
    await flushAll()

    expect(mockHubConnection.invoke).toHaveBeenCalledWith("SubscribeToSessionsTopicAsync")
  })

  it("resubscribes to global sessions topic on reconnect", async () => {
    const { useWeaveSocket } = await import("@/composables/use-signalr-socket")

    await mountComposable(() => useWeaveSocket())
    await flushAll()

    mockHubConnection.invoke.mockClear()

    // Simulate reconnection
    await reconnectedHandler?.()
    await flushAll()

    expect(mockHubConnection.invoke).toHaveBeenCalledWith("SubscribeToSessionsTopicAsync")
  })

  it("dispatches activity_status event to global handlers", async () => {
    const { useWeaveSocket, onGlobalEvent } = await import("@/composables/use-signalr-socket")

    await mountComposable(() => useWeaveSocket())
    await flushAll()

    const handler = vi.fn()
    onGlobalEvent("sessions", handler)

    // Simulate activity_status event from server (matching BuildActivityStatusPayloadAsync shape)
    const wireEvent = {
      type: "activity_status",
      properties: {
        sessionId: "test-session-123",
        activityStatus: "idle",
        capabilities: {
          canPrompt: true,
          canAbort: false,
          canArchive: true,
          canDelete: true,
        },
      },
    }

    eventHandler?.("sessions", 42, wireEvent)

    expect(handler).toHaveBeenCalledWith({
      type: "activity_status",
      payload: {
        sessionId: "test-session-123",
        activityStatus: "idle",
        capabilities: {
          canPrompt: true,
          canAbort: false,
          canArchive: true,
          canDelete: true,
        },
      },
      eventId: 42,
    })
  })

  it("handles activity_status with busy status", async () => {
    const { useWeaveSocket, onGlobalEvent } = await import("@/composables/use-signalr-socket")

    await mountComposable(() => useWeaveSocket())
    await flushAll()

    const handler = vi.fn()
    onGlobalEvent("sessions", handler)

    const wireEvent = {
      type: "activity_status",
      properties: {
        sessionId: "test-session-456",
        activityStatus: "busy",
        capabilities: {
          canPrompt: false,
          canAbort: true,
          canArchive: false,
          canDelete: false,
        },
      },
    }

    eventHandler?.("sessions", 43, wireEvent)

    expect(handler).toHaveBeenCalledWith({
      type: "activity_status",
      payload: {
        sessionId: "test-session-456",
        activityStatus: "busy",
        capabilities: {
          canPrompt: false,
          canAbort: true,
          canArchive: false,
          canDelete: false,
        },
      },
      eventId: 43,
    })
  })

  it("handles activity_status with retry status and retry fields", async () => {
    const { useWeaveSocket, onGlobalEvent } = await import("@/composables/use-signalr-socket")

    await mountComposable(() => useWeaveSocket())
    await flushAll()

    const handler = vi.fn()
    onGlobalEvent("sessions", handler)

    const wireEvent = {
      type: "activity_status",
      properties: {
        sessionId: "test-session-789",
        activityStatus: "retry",
        capabilities: {
          canPrompt: false,
          canAbort: false,
          canArchive: false,
          canDelete: false,
        },
        attempt: 2,
        message: "Connection timeout",
        next: "2026-08-14T12:34:56.789Z",
      },
    }

    eventHandler?.("sessions", 44, wireEvent)

    expect(handler).toHaveBeenCalledWith({
      type: "activity_status",
      payload: {
        sessionId: "test-session-789",
        activityStatus: "retry",
        capabilities: {
          canPrompt: false,
          canAbort: false,
          canArchive: false,
          canDelete: false,
        },
        attempt: 2,
        message: "Connection timeout",
        next: "2026-08-14T12:34:56.789Z",
      },
      eventId: 44,
    })
  })

  it("allows multiple global event handlers for the same topic", async () => {
    const { useWeaveSocket, onGlobalEvent } = await import("@/composables/use-signalr-socket")

    await mountComposable(() => useWeaveSocket())
    await flushAll()

    const handler1 = vi.fn()
    const handler2 = vi.fn()
    onGlobalEvent("sessions", handler1)
    onGlobalEvent("sessions", handler2)

    const wireEvent = {
      type: "activity_status",
      properties: {
        sessionId: "test-session-multi",
        activityStatus: "idle",
        capabilities: createCapabilities({ canPrompt: true, canArchive: true, canDelete: true }),
      },
    }

    eventHandler?.("sessions", 45, wireEvent)

    expect(handler1).toHaveBeenCalled()
    expect(handler2).toHaveBeenCalled()
  })

  it("unsubscribes global event handler correctly", async () => {
    const { useWeaveSocket, onGlobalEvent } = await import("@/composables/use-signalr-socket")

    await mountComposable(() => useWeaveSocket())
    await flushAll()

    const handler = vi.fn()
    const unsubscribe = onGlobalEvent("sessions", handler)

    unsubscribe()

    const wireEvent = {
      type: "activity_status",
      properties: {
        sessionId: "test-session-unsub",
        activityStatus: "idle",
        capabilities: createCapabilities({ canPrompt: true, canArchive: true, canDelete: true }),
      },
    }

    eventHandler?.("sessions", 46, wireEvent)

    expect(handler).not.toHaveBeenCalled()
  })

  it("does not dispatch global events to session-specific listeners", async () => {
    const { useWeaveSocket } = await import("@/composables/use-signalr-socket")

    const snapshot = {
      session: { id: "test-session", title: "Test", status: "idle" },
      messages: [],
      delegations: [],
      activityStatus: "idle",
      lastEventId: null,
      hasMore: false,
      cursor: null,
      isPartial: false,
    }

    mockHubConnection.invoke.mockResolvedValueOnce(snapshot)

    const { result } = await mountComposable(() => useWeaveSocket())

    const sessionEventHandler = vi.fn()
    result.subscribeV2("session:test-session", vi.fn(), sessionEventHandler)
    await flushAll()

    // Send activity_status on "sessions" topic (not "session:test-session")
    const wireEvent = {
      type: "activity_status",
      properties: {
        sessionId: "test-session",
        activityStatus: "busy",
        capabilities: createCapabilities({ canAbort: true }),
      },
    }

    eventHandler?.("sessions", 47, wireEvent)

    // Session-specific handler should NOT receive this event
    expect(sessionEventHandler).not.toHaveBeenCalled()
  })
})

describe("activity_status sessionStatus mapping", () => {
  beforeEach(() => {
    vi.clearAllMocks()
    eventHandler = null
    setActivePinia(createPinia())

    mockHubConnection.state = HubConnectionState.Disconnected
    mockHubConnection.start.mockImplementation(async () => {
      mockHubConnection.state = HubConnectionState.Connected
    })
    mockHubConnection.invoke.mockResolvedValue(undefined)
    mockHubConnection.on.mockImplementation((eventName: string, handler: (...args: unknown[]) => void) => {
      if (eventName === "Event") {
        eventHandler = handler as (topic: string, eventId: number | null, data: unknown) => void
      }
    })
  })

  afterEach(async () => {
    const { _resetForTesting } = await import("@/composables/use-signalr-socket")
    _resetForTesting()
  })

  it("maps activityStatus 'idle' to sessionStatus 'idle'", async () => {
    const { useWeaveSocket } = await import("@/composables/use-signalr-socket")
    const { useSessionActivityUpdates } = await import("@/composables/use-session-activity-updates")
    const { useSessionsStore } = await import("@/stores/sessions")

    await mountComposable(() => {
      useWeaveSocket()
      useSessionActivityUpdates()
    })
    await flushAll()

    const sessionsStore = useSessionsStore()
    sessionsStore.setSessions([{
      session: { id: "test-session", title: "Test", time: { created: 1000, updated: 1000 }, tags: [] },
      sessionStatus: "active",
      activityStatus: "busy",
      lifecycleStatus: "running",
      retentionStatus: "active",
      instanceId: "inst-1",
      workspaceId: "ws-1",
      workspaceDirectory: "/test",
      workspaceDisplayName: null,
      isolationStrategy: "existing",
      instanceStatus: "running",
      parentSessionId: null,
      sourceDirectory: null,
      branch: null,
      archivedAt: null,
      typedInstanceStatus: "running",
      isHidden: false,
      totalTokens: null,
      totalCost: null,
      projectId: null,
      projectName: null,
      harnessType: "opencode",
      capabilities: createCapabilities({ canPrompt: true, canArchive: true, canDelete: true }),
      tags: [],
    }])

    const wireEvent = {
      type: "activity_status",
      properties: {
        sessionId: "test-session",
        activityStatus: "idle",
        capabilities: createCapabilities({ canPrompt: true, canArchive: true, canDelete: true }),
      },
    }

    eventHandler?.("sessions", 1, wireEvent)
    await flushAll()

    const session = sessionsStore.sessions.find((s) => s.session.id === "test-session")
    expect(session?.activityStatus).toBe("idle")
    expect(session?.sessionStatus).toBe("idle")
  })

  it("maps activityStatus 'busy' to sessionStatus 'active'", async () => {
    const { useWeaveSocket } = await import("@/composables/use-signalr-socket")
    const { useSessionActivityUpdates } = await import("@/composables/use-session-activity-updates")
    const { useSessionsStore } = await import("@/stores/sessions")

    await mountComposable(() => {
      useWeaveSocket()
      useSessionActivityUpdates()
    })
    await flushAll()

    const sessionsStore = useSessionsStore()
    sessionsStore.setSessions([{
      session: { id: "test-session", title: "Test", time: { created: 1000, updated: 1000 }, tags: [] },
      sessionStatus: "idle",
      activityStatus: "idle",
      lifecycleStatus: "running",
      retentionStatus: "active",
      instanceId: "inst-1",
      workspaceId: "ws-1",
      workspaceDirectory: "/test",
      workspaceDisplayName: null,
      isolationStrategy: "existing",
      instanceStatus: "running",
      parentSessionId: null,
      sourceDirectory: null,
      branch: null,
      archivedAt: null,
      typedInstanceStatus: "running",
      isHidden: false,
      totalTokens: null,
      totalCost: null,
      projectId: null,
      projectName: null,
      harnessType: "opencode",
      capabilities: createCapabilities({ canAbort: true }),
      tags: [],
    }])

    const wireEvent = {
      type: "activity_status",
      properties: {
        sessionId: "test-session",
        activityStatus: "busy",
        capabilities: createCapabilities({ canAbort: true }),
      },
    }

    eventHandler?.("sessions", 2, wireEvent)
    await flushAll()

    const session = sessionsStore.sessions.find((s) => s.session.id === "test-session")
    expect(session?.activityStatus).toBe("busy")
    expect(session?.sessionStatus).toBe("active")
  })

  it("maps activityStatus 'delegating' to sessionStatus 'active'", async () => {
    const { useWeaveSocket } = await import("@/composables/use-signalr-socket")
    const { useSessionActivityUpdates } = await import("@/composables/use-session-activity-updates")
    const { useSessionsStore } = await import("@/stores/sessions")

    await mountComposable(() => {
      useWeaveSocket()
      useSessionActivityUpdates()
    })
    await flushAll()

    const sessionsStore = useSessionsStore()
    sessionsStore.setSessions([{
      session: { id: "test-session", title: "Test", time: { created: 1000, updated: 1000 }, tags: [] },
      sessionStatus: "idle",
      activityStatus: "idle",
      lifecycleStatus: "running",
      retentionStatus: "active",
      instanceId: "inst-1",
      workspaceId: "ws-1",
      workspaceDirectory: "/test",
      workspaceDisplayName: null,
      isolationStrategy: "existing",
      instanceStatus: "running",
      parentSessionId: null,
      sourceDirectory: null,
      branch: null,
      archivedAt: null,
      typedInstanceStatus: "running",
      isHidden: false,
      totalTokens: null,
      totalCost: null,
      projectId: null,
      projectName: null,
      harnessType: "opencode",
      capabilities: createCapabilities({ canAbort: true }),
      tags: [],
    }])

    const wireEvent = {
      type: "activity_status",
      properties: {
        sessionId: "test-session",
        activityStatus: "delegating",
        capabilities: createCapabilities({ canAbort: true }),
      },
    }

    eventHandler?.("sessions", 3, wireEvent)
    await flushAll()

    const session = sessionsStore.sessions.find((s) => s.session.id === "test-session")
    expect(session?.activityStatus).toBe("delegating")
    expect(session?.sessionStatus).toBe("active")
  })

  it("maps activityStatus 'retry' to sessionStatus 'active'", async () => {
    const { useWeaveSocket } = await import("@/composables/use-signalr-socket")
    const { useSessionActivityUpdates } = await import("@/composables/use-session-activity-updates")
    const { useSessionsStore } = await import("@/stores/sessions")

    await mountComposable(() => {
      useWeaveSocket()
      useSessionActivityUpdates()
    })
    await flushAll()

    const sessionsStore = useSessionsStore()
    sessionsStore.setSessions([{
      session: { id: "test-session", title: "Test", time: { created: 1000, updated: 1000 }, tags: [] },
      sessionStatus: "idle",
      activityStatus: "idle",
      lifecycleStatus: "running",
      retentionStatus: "active",
      instanceId: "inst-1",
      workspaceId: "ws-1",
      workspaceDirectory: "/test",
      workspaceDisplayName: null,
      isolationStrategy: "existing",
      instanceStatus: "running",
      parentSessionId: null,
      sourceDirectory: null,
      branch: null,
      archivedAt: null,
      typedInstanceStatus: "running",
      isHidden: false,
      totalTokens: null,
      totalCost: null,
      projectId: null,
      projectName: null,
      harnessType: "opencode",
      capabilities: createCapabilities(),
      tags: [],
    }])

    const wireEvent = {
      type: "activity_status",
      properties: {
        sessionId: "test-session",
        activityStatus: "retry",
        capabilities: createCapabilities(),
      },
    }

    eventHandler?.("sessions", 4, wireEvent)
    await flushAll()

    const session = sessionsStore.sessions.find((s) => s.session.id === "test-session")
    expect(session?.activityStatus).toBe("retry")
    expect(session?.sessionStatus).toBe("active")
  })

  it("preserves lifecycle state 'stopped' when receiving idle activity event", async () => {
    const { useWeaveSocket } = await import("@/composables/use-signalr-socket")
    const { useSessionActivityUpdates } = await import("@/composables/use-session-activity-updates")
    const { useSessionsStore } = await import("@/stores/sessions")

    await mountComposable(() => {
      useWeaveSocket()
      useSessionActivityUpdates()
    })
    await flushAll()

    const sessionsStore = useSessionsStore()
    sessionsStore.setSessions([{
      session: { id: "test-session", title: "Test", time: { created: 1000, updated: 1000 }, tags: [] },
      sessionStatus: "stopped",
      activityStatus: "idle",
      lifecycleStatus: "stopped",
      retentionStatus: "active",
      instanceId: "inst-1",
      workspaceId: "ws-1",
      workspaceDirectory: "/test",
      workspaceDisplayName: null,
      isolationStrategy: "existing",
      instanceStatus: "stopped",
      parentSessionId: null,
      sourceDirectory: null,
      branch: null,
      archivedAt: null,
      typedInstanceStatus: "stopped",
      isHidden: false,
      totalTokens: null,
      totalCost: null,
      projectId: null,
      projectName: null,
      harnessType: "opencode",
      capabilities: createCapabilities({ canArchive: true, canDelete: true }),
      tags: [],
    }])

    const wireEvent = {
      type: "activity_status",
      properties: {
        sessionId: "test-session",
        activityStatus: "idle",
        capabilities: createCapabilities({ canArchive: true, canDelete: true }),
      },
    }

    eventHandler?.("sessions", 5, wireEvent)
    await flushAll()

    const session = sessionsStore.sessions.find((s) => s.session.id === "test-session")
    expect(session?.activityStatus).toBe("idle")
    expect(session?.sessionStatus).toBe("stopped") // Should NOT change to "idle"
  })

  it("preserves lifecycle state 'completed' when receiving busy activity event", async () => {
    const { useWeaveSocket } = await import("@/composables/use-signalr-socket")
    const { useSessionActivityUpdates } = await import("@/composables/use-session-activity-updates")
    const { useSessionsStore } = await import("@/stores/sessions")

    await mountComposable(() => {
      useWeaveSocket()
      useSessionActivityUpdates()
    })
    await flushAll()

    const sessionsStore = useSessionsStore()
    sessionsStore.setSessions([{
      session: { id: "test-session", title: "Test", time: { created: 1000, updated: 1000 }, tags: [] },
      sessionStatus: "completed",
      activityStatus: "idle",
      lifecycleStatus: "completed",
      retentionStatus: "active",
      instanceId: "inst-1",
      workspaceId: "ws-1",
      workspaceDirectory: "/test",
      workspaceDisplayName: null,
      isolationStrategy: "existing",
      instanceStatus: "completed",
      parentSessionId: null,
      sourceDirectory: null,
      branch: null,
      archivedAt: null,
      typedInstanceStatus: "completed",
      isHidden: false,
      totalTokens: null,
      totalCost: null,
      projectId: null,
      projectName: null,
      harnessType: "opencode",
      capabilities: createCapabilities({ canArchive: true, canDelete: true }),
      tags: [],
    }])

    const wireEvent = {
      type: "activity_status",
      properties: {
        sessionId: "test-session",
        activityStatus: "busy",
        capabilities: createCapabilities({ canArchive: true, canDelete: true }),
      },
    }

    eventHandler?.("sessions", 6, wireEvent)
    await flushAll()

    const session = sessionsStore.sessions.find((s) => s.session.id === "test-session")
    expect(session?.activityStatus).toBe("busy")
    expect(session?.sessionStatus).toBe("completed") // Should NOT change to "active"
  })

  it("preserves lifecycle state 'error' when receiving idle activity event", async () => {
    const { useWeaveSocket } = await import("@/composables/use-signalr-socket")
    const { useSessionActivityUpdates } = await import("@/composables/use-session-activity-updates")
    const { useSessionsStore } = await import("@/stores/sessions")

    await mountComposable(() => {
      useWeaveSocket()
      useSessionActivityUpdates()
    })
    await flushAll()

    const sessionsStore = useSessionsStore()
    sessionsStore.setSessions([{
      session: { id: "test-session", title: "Test", time: { created: 1000, updated: 1000 }, tags: [] },
      sessionStatus: "error",
      activityStatus: "idle",
      lifecycleStatus: "error",
      retentionStatus: "active",
      instanceId: "inst-1",
      workspaceId: "ws-1",
      workspaceDirectory: "/test",
      workspaceDisplayName: null,
      isolationStrategy: "existing",
      instanceStatus: "error",
      parentSessionId: null,
      sourceDirectory: null,
      branch: null,
      archivedAt: null,
      typedInstanceStatus: "error",
      isHidden: false,
      totalTokens: null,
      totalCost: null,
      projectId: null,
      projectName: null,
      harnessType: "opencode",
      capabilities: createCapabilities({ canArchive: true, canDelete: true }),
      tags: [],
    }])

    const wireEvent = {
      type: "activity_status",
      properties: {
        sessionId: "test-session",
        activityStatus: "idle",
        capabilities: createCapabilities({ canArchive: true, canDelete: true }),
      },
    }

    eventHandler?.("sessions", 7, wireEvent)
    await flushAll()

    const session = sessionsStore.sessions.find((s) => s.session.id === "test-session")
    expect(session?.activityStatus).toBe("idle")
    expect(session?.sessionStatus).toBe("error") // Should NOT change to "idle"
  })

  it("preserves lifecycle state 'disconnected' when receiving busy activity event", async () => {
    const { useWeaveSocket } = await import("@/composables/use-signalr-socket")
    const { useSessionActivityUpdates } = await import("@/composables/use-session-activity-updates")
    const { useSessionsStore } = await import("@/stores/sessions")

    await mountComposable(() => {
      useWeaveSocket()
      useSessionActivityUpdates()
    })
    await flushAll()

    const sessionsStore = useSessionsStore()
    sessionsStore.setSessions([{
      session: { id: "test-session", title: "Test", time: { created: 1000, updated: 1000 }, tags: [] },
      sessionStatus: "disconnected",
      activityStatus: "idle",
      lifecycleStatus: "disconnected",
      retentionStatus: "active",
      instanceId: "inst-1",
      workspaceId: "ws-1",
      workspaceDirectory: "/test",
      workspaceDisplayName: null,
      isolationStrategy: "existing",
      instanceStatus: "disconnected",
      parentSessionId: null,
      sourceDirectory: null,
      branch: null,
      archivedAt: null,
      typedInstanceStatus: "disconnected",
      isHidden: false,
      totalTokens: null,
      totalCost: null,
      projectId: null,
      projectName: null,
      harnessType: "opencode",
      capabilities: createCapabilities({ canArchive: true, canDelete: true }),
      tags: [],
    }])

    const wireEvent = {
      type: "activity_status",
      properties: {
        sessionId: "test-session",
        activityStatus: "busy",
        capabilities: createCapabilities({ canArchive: true, canDelete: true }),
      },
    }

    eventHandler?.("sessions", 8, wireEvent)
    await flushAll()

    const session = sessionsStore.sessions.find((s) => s.session.id === "test-session")
    expect(session?.activityStatus).toBe("busy")
    expect(session?.sessionStatus).toBe("disconnected") // Should NOT change to "active"
  })

  it("preserves lifecycle state 'resuming' when receiving idle activity event", async () => {
    const { useWeaveSocket } = await import("@/composables/use-signalr-socket")
    const { useSessionActivityUpdates } = await import("@/composables/use-session-activity-updates")
    const { useSessionsStore } = await import("@/stores/sessions")

    await mountComposable(() => {
      useWeaveSocket()
      useSessionActivityUpdates()
    })
    await flushAll()

    const sessionsStore = useSessionsStore()
    sessionsStore.setSessions([{
      session: { id: "test-session", title: "Test", time: { created: 1000, updated: 1000 }, tags: [] },
      sessionStatus: "resuming",
      activityStatus: "idle",
      lifecycleStatus: "resuming",
      retentionStatus: "active",
      instanceId: "inst-1",
      workspaceId: "ws-1",
      workspaceDirectory: "/test",
      workspaceDisplayName: null,
      isolationStrategy: "existing",
      instanceStatus: "resuming",
      parentSessionId: null,
      sourceDirectory: null,
      branch: null,
      archivedAt: null,
      typedInstanceStatus: "resuming",
      isHidden: false,
      totalTokens: null,
      totalCost: null,
      projectId: null,
      projectName: null,
      harnessType: "opencode",
      capabilities: createCapabilities(),
      tags: [],
    }])

    const wireEvent = {
      type: "activity_status",
      properties: {
        sessionId: "test-session",
        activityStatus: "idle",
        capabilities: createCapabilities(),
      },
    }

    eventHandler?.("sessions", 9, wireEvent)
    await flushAll()

    const session = sessionsStore.sessions.find((s) => s.session.id === "test-session")
    expect(session?.activityStatus).toBe("idle")
    expect(session?.sessionStatus).toBe("resuming") // Should NOT change to "idle"
  })
})
