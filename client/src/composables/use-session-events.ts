import {
  computed,
  readonly,
  ref,
  shallowRef,
  toValue,
  watch,
  type MaybeRefOrGetter,
  type Ref,
  type ShallowRef,
} from "vue"
import type {
  AccumulatedMessage,
  CommittedSessionEvent,
  DelegationDto,
  WebSocketEvent,
} from "@/lib/api-types"
import { apiFetch } from "@/lib/api-client"
import { applyDelegationCreated, applyDelegationUpdated } from "@/lib/delegation-state"
import { applyPartUpdate, applyTextDelta, ensureMessage, mergeMessageUpdate } from "@/lib/event-state"
import { convertFleetMessageToAccumulated, prependMessages, type FleetMessage } from "@/lib/pagination-utils"
import { sessionCache } from "@/lib/session-cache"
import { addSessionSyncListener } from "@/lib/session-sync"
import { useMessagePagination } from "@/composables/use-message-pagination"
import { clearPendingPrompts, clearSentPrompts } from "@/composables/use-send-prompt"
import { isWeaveSocketConnected, onDisconnect, onReconnect, useWeaveSocket } from "@/composables/use-weave-socket"
import { diagLog } from "@/lib/message-diagnostics"
import { useSessionsStore } from "@/stores/sessions"

export type SessionConnectionStatus =
  | "connecting"
  | "connected"
  | "recovering"
  | "disconnected"
  | "error"
  | "abandoned"

export interface UseSessionEventsResult {
  messages: Ref<readonly AccumulatedMessage[]>
  delegations: Ref<readonly DelegationDto[]>
  status: Readonly<ShallowRef<SessionConnectionStatus>>
  sessionStatus: Readonly<ShallowRef<"idle" | "busy">>
  error: Readonly<ShallowRef<string | undefined>>
  forceBusy: () => void
  forceIdle: () => void
  reconnect: () => void
  reconnectAttempt: Readonly<ShallowRef<number>>
  hasMoreMessages: Readonly<ShallowRef<boolean>>
  isLoadingOlder: Readonly<ShallowRef<boolean>>
  loadOlderMessages: () => Promise<void>
  totalMessageCount: Readonly<ShallowRef<number | null>>
  loadOlderError: Readonly<ShallowRef<string | null>>
  cacheHit: Readonly<ShallowRef<boolean>>
  initialScrollPosition: Readonly<ShallowRef<{ scrollTop: number; scrollHeight: number } | null>>
  scrollPositionRef: ShallowRef<{ scrollTop: number; scrollHeight: number } | null>
}

interface SessionEventState {
  messages: Ref<AccumulatedMessage[]>
  delegations: Ref<DelegationDto[]>
  status: ShallowRef<SessionConnectionStatus>
  sessionStatus: ShallowRef<"idle" | "busy">
  error: ShallowRef<string | undefined>
  onAgentSwitch: ShallowRef<((agent: string) => void) | undefined>
  lastSequenceNumber: ShallowRef<number | null>
  scheduleIdleFallback?: () => void
  clearIdleFallback?: () => void
  syncSessionStore?: (patch: SessionStorePatch) => void
}

type SessionStorePatch = Partial<{
  activityStatus: "busy" | "idle"
  lifecycleStatus: "running" | "stopped" | "completed" | "disconnected" | "error"
  sessionStatus: "active" | "idle" | "stopped" | "completed" | "disconnected" | "error" | "waiting_input"
}>

const MAX_MESSAGES = 500
const IDLE_FALLBACK_MS = 2500

export function useSessionEvents(
  sessionId: MaybeRefOrGetter<string>,
  instanceId: MaybeRefOrGetter<string>,
  onAgentSwitch?: MaybeRefOrGetter<((agent: string) => void) | undefined>,
  suppressAutoScrollRef?: ShallowRef<boolean>,
): UseSessionEventsResult {
  const sessionsStore = useSessionsStore()
  const messages = ref<AccumulatedMessage[]>([])
  const delegations = ref<DelegationDto[]>([])
  const status = shallowRef<SessionConnectionStatus>("connecting")
  const sessionStatus = shallowRef<"idle" | "busy">("idle")
  const error = shallowRef<string | undefined>(undefined)
  const cacheHit = shallowRef(false)
  const initialScrollPosition = shallowRef<{ scrollTop: number; scrollHeight: number } | null>(null)
  const scrollPositionRef = shallowRef<{ scrollTop: number; scrollHeight: number } | null>(null)
  const reconnectAttempt = shallowRef(0)
  const lastSequenceNumber = shallowRef<number | null>(null)
  const onAgentSwitchRef = shallowRef<((agent: string) => void) | undefined>(toValue(onAgentSwitch))
  let idleFallbackTimer: ReturnType<typeof setTimeout> | null = null
  let skipInitialGapFill = true

  const currentSessionId = computed(() => toValue(sessionId))
  const currentInstanceId = computed(() => toValue(instanceId))
  const pagination = useMessagePagination()
  const { subscribe } = useWeaveSocket()

  const loadCommittedEventsSinceRef = shallowRef<((afterSequenceNumber: number | null, signal?: AbortSignal) => Promise<void>) | null>(null)
  const loadDelegationsRef = shallowRef<((signal?: AbortSignal) => Promise<void>) | null>(null)

  watch(
    () => toValue(onAgentSwitch),
    (nextOnAgentSwitch) => {
      onAgentSwitchRef.value = nextOnAgentSwitch
    },
  )

  watch(
    () => [currentSessionId.value, currentInstanceId.value] as const,
    ([activeSessionId, activeInstanceId], _, onCleanup) => {
      let disposed = false
      const abortController = new AbortController()
      const { signal } = abortController

      function isActive(): boolean {
        return !disposed
          && activeSessionId === currentSessionId.value
          && activeInstanceId === currentInstanceId.value
      }

      async function loadAllMessages(loadSignal?: AbortSignal): Promise<void> {
        if (!activeSessionId || !activeInstanceId) {
          return
        }

        if (isActive()) {
          cacheHit.value = false
          initialScrollPosition.value = null
        }

        try {
          const response = await apiFetch(`/api/sessions/${encodeURIComponent(activeSessionId)}/messages`, loadSignal ? { signal: loadSignal } : undefined)
          if (!response.ok) {
            return
          }

          const data = (await response.json()) as { messages?: FleetMessage[] }
          if (!data.messages?.length || loadSignal?.aborted || !isActive()) {
            return
          }

          messages.value = data.messages
            .map(convertFleetMessageToAccumulated)
            .slice(-MAX_MESSAGES)
          pagination.resetPagination()
        } catch (loadError) {
          if (loadError instanceof DOMException && loadError.name === "AbortError") {
            return
          }
        }
      }

        function applyCommittedEvents(events: CommittedSessionEvent[]): void {
        if (events.length === 0) {
          return
        }

        const orderedEvents = [...events].sort((left, right) => left.sequenceNumber - right.sequenceNumber)
        const state: SessionEventState = {
          messages,
          delegations,
          status,
          sessionStatus,
          error,
          onAgentSwitch: onAgentSwitchRef,
          lastSequenceNumber,
          scheduleIdleFallback,
          clearIdleFallback,
          syncSessionStore: (patch) => {
            syncSessionStore(activeSessionId, patch)
          },
        }

        for (const committedEvent of orderedEvents) {
          handleEvent(
            {
              type: committedEvent.type,
              sequenceNumber: committedEvent.sequenceNumber,
              properties: committedEvent.payload,
            },
            activeSessionId,
            state,
          )
        }
      }

      async function loadCommittedEventsSince(
        afterSequenceNumber: number | null,
        loadSignal?: AbortSignal,
      ): Promise<void> {
        if (!activeSessionId || !activeInstanceId) {
          return
        }

        if (afterSequenceNumber == null) {
          await loadAllMessages(loadSignal)
          return
        }

        try {
          const params = new URLSearchParams({
            afterSequenceNumber: String(afterSequenceNumber),
          })

          const response = await apiFetch(
            `/api/sessions/${encodeURIComponent(activeSessionId)}/committed-events?${params.toString()}`,
            loadSignal ? { signal: loadSignal } : undefined,
          )

          if (!response.ok) {
            const savedScroll = scrollPositionRef.value
            await loadAllMessages(loadSignal)
            if (savedScroll && !loadSignal?.aborted && isActive()) {
              initialScrollPosition.value = savedScroll
            }
            return
          }

          const data = (await response.json()) as { events?: CommittedSessionEvent[] }
          if (!data.events?.length || loadSignal?.aborted || !isActive()) {
            return
          }

          applyCommittedEvents(data.events)
        } catch (loadError) {
          if (loadError instanceof DOMException && loadError.name === "AbortError") {
            return
          }

          const savedScroll = scrollPositionRef.value
          await loadAllMessages(loadSignal)
          if (savedScroll && !loadSignal?.aborted && isActive()) {
            initialScrollPosition.value = savedScroll
          }
        }
      }

      async function loadInitialMessages(loadSignal?: AbortSignal): Promise<void> {
        if (!activeSessionId || !activeInstanceId) {
          return
        }

        try {
          const accumulated = await pagination.loadInitialMessages(activeSessionId, activeInstanceId, loadSignal)
          if (!loadSignal?.aborted && isActive()) {
            messages.value = accumulated
          }
        } catch (loadError) {
          if (loadError instanceof DOMException && loadError.name === "AbortError") {
            return
          }
        }
      }

      async function loadDelegations(loadSignal?: AbortSignal): Promise<void> {
        if (!activeSessionId || !activeInstanceId) {
          return
        }

        try {
          const response = await apiFetch(
            `/api/sessions/${encodeURIComponent(activeSessionId)}/delegations`,
            loadSignal ? { signal: loadSignal } : undefined,
          )
          if (!response.ok) {
            return
          }

          const data = (await response.json()) as DelegationDto[]
          if (!loadSignal?.aborted && isActive()) {
            delegations.value = Array.isArray(data) ? data : []
          }
        } catch (loadError) {
          if (loadError instanceof DOMException && loadError.name === "AbortError") {
            return
          }
        }
      }

      loadCommittedEventsSinceRef.value = loadCommittedEventsSince
      loadDelegationsRef.value = loadDelegations
      clearIdleFallback()
      skipInitialGapFill = !isWeaveSocketConnected()

      messages.value = []
      delegations.value = []
      sessionStatus.value = "idle"
      status.value = "connecting"
      error.value = undefined
      lastSequenceNumber.value = null

      if (suppressAutoScrollRef) {
        suppressAutoScrollRef.value = false
      }

      if (!activeSessionId || !activeInstanceId) {
        status.value = "connected"
        pagination.resetPagination()

        onCleanup(() => {
          disposed = true
          abortController.abort()
          clearIdleFallback()
        })

        return
      }

      const cached = sessionCache.get(activeSessionId, activeInstanceId)
      if (cached) {
        if (suppressAutoScrollRef) {
          suppressAutoScrollRef.value = true
        }

        messages.value = cached.messages
        delegations.value = cached.delegations
        sessionStatus.value = cached.sessionStatus
        lastSequenceNumber.value = cached.lastSequenceNumber
        pagination.hydratePagination(cached.pagination)
        cacheHit.value = true
        initialScrollPosition.value = {
          scrollTop: cached.scrollPosition,
          scrollHeight: cached.scrollHeight,
        }

        void Promise.all([
          loadCommittedEventsSince(cached.lastSequenceNumber, signal),
          loadDelegations(signal),
        ]).finally(() => {
          if (!signal.aborted && isActive()) {
            status.value = "connected"
          }
        })
      } else {
        cacheHit.value = false
        initialScrollPosition.value = null

        void Promise.all([
          loadInitialMessages(signal),
          loadDelegations(signal),
        ]).finally(() => {
          if (!signal.aborted && isActive()) {
            status.value = "connected"
          }
        })
      }

      const topic = `session:${activeSessionId}`
      const unsubscribeTopic = subscribe([topic], (_topic: string, rawData: unknown) => {
        if (!isActive()) {
          return
        }

        const event = rawData as WebSocketEvent
        handleEvent(event, activeSessionId, {
          messages,
          delegations,
          status,
          sessionStatus,
          error,
          onAgentSwitch: onAgentSwitchRef,
          lastSequenceNumber,
          scheduleIdleFallback,
          clearIdleFallback,
          syncSessionStore: (patch) => {
            syncSessionStore(activeSessionId, patch)
          },
        })
      })

      const unsubscribeSessionSync = addSessionSyncListener((operation) => {
        if (!isActive()) {
          return
        }

        if (operation.type === "remove") {
          if (operation.sessionId !== activeSessionId) {
            return
          }

          messages.value = []
          delegations.value = []
          lastSequenceNumber.value = null
          return
        }

        if (operation.session.session.id !== activeSessionId) {
          return
        }

        const incomingActivityStatus = operation.session.activityStatus
        if (incomingActivityStatus === "busy") {
          sessionStatus.value = "busy"
          scheduleIdleFallback()
          return
        }

        if (incomingActivityStatus === "idle") {
          clearIdleFallback()
          sessionStatus.value = "idle"
        }
      })

      const unsubscribeReconnect = onReconnect(() => {
        if (!isActive()) {
          return
        }

        if (skipInitialGapFill) {
          skipInitialGapFill = false
          return
        }

        void Promise.all([
          loadCommittedEventsSince(lastSequenceNumber.value),
          loadDelegations(),
        ]).finally(() => {
          if (isActive()) {
            stateSyncRunning(activeSessionId, sessionStatus.value)
          }
        })
      })

      const unsubscribeDisconnect = onDisconnect(() => {
        if (!isActive()) {
          return
        }

        status.value = "disconnected"
        error.value = undefined
        stateSyncDisconnected(activeSessionId)
      })

      onCleanup(() => {
        disposed = true
        abortController.abort()
        clearIdleFallback()
        unsubscribeTopic()
        unsubscribeSessionSync()
        unsubscribeReconnect()
        unsubscribeDisconnect()

        if (loadCommittedEventsSinceRef.value === loadCommittedEventsSince) {
          loadCommittedEventsSinceRef.value = null
        }

        if (loadDelegationsRef.value === loadDelegations) {
          loadDelegationsRef.value = null
        }

        if (messages.value.length > 0) {
          sessionCache.set(activeSessionId, activeInstanceId, {
            messages: messages.value,
            delegations: delegations.value,
            scrollPosition: scrollPositionRef.value?.scrollTop ?? 0,
            scrollHeight: scrollPositionRef.value?.scrollHeight ?? 0,
            sessionStatus: sessionStatus.value,
            lastSequenceNumber: lastSequenceNumber.value,
            pagination: pagination.snapshotPagination(),
            timestamp: Date.now(),
          })
        }
      })
    },
    { immediate: true },
  )

  function forceIdle(): void {
    clearIdleFallback()
    sessionStatus.value = "idle"
    clearPendingPrompts(currentSessionId.value)
    clearSentPrompts(currentSessionId.value)
    syncSessionStore(currentSessionId.value, { activityStatus: "idle", sessionStatus: "idle" })
  }

  function forceBusy(): void {
    sessionStatus.value = "busy"
    scheduleIdleFallback()
    syncSessionStore(currentSessionId.value, {
      activityStatus: "busy",
      lifecycleStatus: "running",
      sessionStatus: "active",
    })
  }

  function syncSessionStore(
    targetSessionId: string | undefined,
    patch: SessionStorePatch,
  ): void {
    if (!targetSessionId) {
      return
    }

    const session = sessionsStore.sessions.find((item) => item.session.id === targetSessionId)
    if (!session) {
      return
    }

    Object.assign(session, patch)
  }

  function stateSyncDisconnected(targetSessionId: string | undefined): void {
    syncSessionStore(targetSessionId, {
      activityStatus: "idle",
      lifecycleStatus: "disconnected",
      sessionStatus: "disconnected",
    })
  }

  function stateSyncRunning(
    targetSessionId: string | undefined,
    currentSessionStatus: "idle" | "busy",
  ): void {
    syncSessionStore(targetSessionId, {
      activityStatus: currentSessionStatus,
      lifecycleStatus: "running",
      sessionStatus: currentSessionStatus === "busy" ? "active" : "idle",
    })
  }

  async function loadOlderMessages(): Promise<void> {
    if (!currentSessionId.value || !currentInstanceId.value) {
      return
    }

    const older = await pagination.loadOlderMessages(currentSessionId.value, currentInstanceId.value)
    if (older.length > 0) {
      messages.value = prependMessages(messages.value, older)
    }
  }

  function clearIdleFallback(): void {
    if (idleFallbackTimer === null) {
      return
    }

    clearTimeout(idleFallbackTimer)
    idleFallbackTimer = null
  }

  function scheduleIdleFallback(): void {
    clearIdleFallback()
    idleFallbackTimer = setTimeout(() => {
      idleFallbackTimer = null
      forceIdle()
    }, IDLE_FALLBACK_MS)
  }

  function reconnect(): void {
    if (!currentSessionId.value || !currentInstanceId.value) {
      return
    }

    status.value = "recovering"

    void Promise.all([
      loadCommittedEventsSinceRef.value?.(lastSequenceNumber.value),
      loadDelegationsRef.value?.(),
    ]).then(() => {
      if (currentSessionId.value && currentInstanceId.value) {
        stateSyncRunning(currentSessionId.value, sessionStatus.value)
        status.value = "connected"
        error.value = undefined
      }
    })
  }

  const readonlyMessages = computed<readonly AccumulatedMessage[]>(() => messages.value)
  const readonlyDelegations = computed<readonly DelegationDto[]>(() => delegations.value)

  return {
    messages: readonlyMessages,
    delegations: readonlyDelegations,
    status: readonly(status),
    sessionStatus: readonly(sessionStatus),
    error: readonly(error),
    forceBusy,
    forceIdle,
    reconnect,
    reconnectAttempt: readonly(reconnectAttempt),
    hasMoreMessages: pagination.hasMore,
    isLoadingOlder: pagination.isLoadingOlder,
    loadOlderMessages,
    totalMessageCount: pagination.totalCount,
    loadOlderError: pagination.loadError,
    cacheHit: readonly(cacheHit),
    initialScrollPosition: readonly(initialScrollPosition),
    scrollPositionRef,
  }
}

export function handleEvent(
  event: WebSocketEvent,
  sessionId: string,
  state: SessionEventState,
): void {
  const { type, properties } = event

  if (typeof event.sequenceNumber === "number") {
    state.lastSequenceNumber.value = Math.max(state.lastSequenceNumber.value ?? 0, event.sequenceNumber)
  }

  const delegationId = properties?.delegationId ?? properties?.DelegationId
  const parentToolCallId = properties?.parentToolCallId ?? properties?.ParentToolCallId
  const childSessionId = properties?.childSessionId ?? properties?.ChildSessionId
  const delegationTitle = properties?.title ?? properties?.Title
  const delegationStatus = properties?.status ?? properties?.Status
  const delegationCreatedAt = properties?.createdAt ?? properties?.CreatedAt

  if (type === "server.connected") {
    state.status.value = "connected"
    return
  }

  if (type === "error") {
    state.error.value = properties?.message ?? "Unknown error"
    state.status.value = "error"
    return
  }

  if (type === "activity_status") {
    const rawActivityStatus = properties?.activityStatus

    if (rawActivityStatus === "idle") {
      clearSentPrompts(sessionId)
      clearPendingPrompts(sessionId)
      clearIdleFallbackForState(state)
      state.sessionStatus.value = "idle"
      state.syncSessionStore?.({
        activityStatus: "idle",
        lifecycleStatus: "running",
        sessionStatus: "idle",
      })
    } else if (rawActivityStatus === "busy" || rawActivityStatus === "working") {
      state.sessionStatus.value = "busy"
      scheduleIdleFallbackForState(state)
      state.syncSessionStore?.({
        activityStatus: "busy",
        lifecycleStatus: "running",
        sessionStatus: "active",
      })
    }

    return
  }

  if (type === "session.status") {
    const rawStatus = properties?.status
    const statusType = typeof rawStatus === "string"
      ? rawStatus
      : rawStatus?.type

    if (statusType === "idle") {
      clearSentPrompts(sessionId)
      clearPendingPrompts(sessionId)
      clearIdleFallbackForState(state)
      state.sessionStatus.value = "idle"
      state.syncSessionStore?.({
        activityStatus: "idle",
        lifecycleStatus: "running",
        sessionStatus: "idle",
      })
    } else if (statusType === "busy" || statusType === "working") {
      state.sessionStatus.value = "busy"
      scheduleIdleFallbackForState(state)
      state.syncSessionStore?.({
        activityStatus: "busy",
        lifecycleStatus: "running",
        sessionStatus: "active",
      })
    }
    return
  }

  if (type === "session.idle") {
    clearSentPrompts(sessionId)
    clearPendingPrompts(sessionId)
    clearIdleFallbackForState(state)
    state.sessionStatus.value = "idle"
    state.syncSessionStore?.({
      activityStatus: "idle",
      lifecycleStatus: "running",
      sessionStatus: "idle",
    })
    return
  }

  if (type === "message.updated") {
    scheduleIdleFallbackForState(state)
    const info = properties?.info
    if (!info?.id) {
      return
    }

    const hasParts = Array.isArray(properties?.parts) && properties.parts.length > 0
    diagLog("msg.updated", `id=${info.id} role=${info.role ?? "?"} hasParts=${hasParts}`, {
      messageId: info.id,
      role: info.role,
      partsCount: Array.isArray(properties?.parts) ? properties.parts.length : 0,
      existingMessageCount: state.messages.value.length,
    })

    const nextMessages = mergeMessageUpdate(
      ensureMessage(state.messages.value, info),
      { ...info, parts: properties?.parts },
    )
    state.messages.value = nextMessages.length > MAX_MESSAGES ? nextMessages.slice(-MAX_MESSAGES) : nextMessages
    return
  }

  if (type === "delegation.created") {
    if (!delegationId) {
      return
    }

    state.delegations.value = applyDelegationCreated(state.delegations.value, {
      delegationId,
      parentToolCallId,
      childSessionId,
      title: delegationTitle,
      status: delegationStatus,
      createdAt: delegationCreatedAt,
    })
    return
  }

  if (type === "delegation.updated") {
    if (!delegationId) {
      return
    }

    state.delegations.value = applyDelegationUpdated(state.delegations.value, {
      delegationId,
      parentToolCallId,
      childSessionId,
      title: delegationTitle,
      status: delegationStatus,
      createdAt: delegationCreatedAt,
    })
    return
  }

  if (type === "message.part.updated") {
    scheduleIdleFallbackForState(state)
    const part = properties?.part
    if (!part?.messageID) {
      diagLog("msg.part.updated", "DROPPED: missing part.messageID", { properties })
      return
    }

    const msgExists = state.messages.value.some((m) => m.messageId === part.messageID)
    diagLog("msg.part.updated", `msgId=${part.messageID} partType=${part.type} msgExists=${msgExists}`, {
      messageId: part.messageID,
      partId: part.id,
      partType: part.type,
      textLength: typeof part.text === "string" ? part.text.length : 0,
    })

    state.messages.value = applyPartUpdate(state.messages.value, { ...part, sessionID: sessionId })

    if (part.type === "tool" && part.state?.status === "completed") {
      if (part.tool === "plan_exit") {
        state.onAgentSwitch.value?.("build")
      } else if (part.tool === "plan_enter") {
        state.onAgentSwitch.value?.("plan")
      }
    }
    return
  }

  if (type === "message.part.delta") {
    scheduleIdleFallbackForState(state)
    const { messageID, partID, field, delta } = properties ?? {}
    if (field !== "text" || !messageID || !partID) {
      return
    }

    state.messages.value = applyTextDelta(state.messages.value, messageID, partID, sessionId, delta ?? "")
  }
}

function scheduleIdleFallbackForState(state: SessionEventState): void {
  const scheduler = (state as SessionEventState & { scheduleIdleFallback?: () => void }).scheduleIdleFallback
  scheduler?.()
}

function clearIdleFallbackForState(state: SessionEventState): void {
  const clearer = (state as SessionEventState & { clearIdleFallback?: () => void }).clearIdleFallback
  clearer?.()
}
