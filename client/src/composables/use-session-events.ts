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
  SessionListItem,
  WebSocketEvent,
} from "@/lib/api-types"
import type { SessionStreamStatus } from "@/lib/domain-event-reducer"
import { apiFetch } from "@/lib/api-client"
import { applyDelegationCreated, applyDelegationUpdated } from "@/lib/delegation-state"
import { applyPartUpdate, applyTextDelta, ensureMessage, mergeMessageUpdate } from "@/lib/event-state"
import { convertFleetMessageToAccumulated, prependMessages, type FleetMessage } from "@/lib/pagination-utils"
import { sessionCache } from "@/lib/session-cache"
import { addSessionSyncListener } from "@/lib/session-sync"
import { useMessagePagination } from "@/composables/use-message-pagination"
import { clearPendingPrompts, clearSentPrompts, confirmSentPrompt } from "@/composables/use-send-prompt"
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
  sessionStatus: Readonly<ShallowRef<SessionStreamStatus>>
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

type SessionStreamExplicitStatus = "idle" | "busy" | "delegating"

interface SessionEventState {
  messages: Ref<AccumulatedMessage[]>
  delegations: Ref<DelegationDto[]>
  status: ShallowRef<SessionConnectionStatus>
  sessionStatus: ShallowRef<SessionStreamStatus>
  explicitStatus: ShallowRef<SessionStreamExplicitStatus>
  error: ShallowRef<string | undefined>
  onAgentSwitch: ShallowRef<((agent: string) => void) | undefined>
  lastEventId: ShallowRef<number | null>
  scheduleIdleFallback?: () => void
  clearIdleFallback?: () => void
  maybeResolveIdleFallback?: () => void
  syncSessionStore?: (patch: SessionStorePatch) => void
}

type SessionStorePatch = Partial<Pick<
  SessionListItem,
  "activityStatus" | "capabilities" | "lifecycleStatus" | "sessionStatus"
>>
type SessionActionCapabilities = NonNullable<SessionListItem["capabilities"]>

const MAX_MESSAGES = 500
// Flag-off mode still uses the legacy v1 stream, which does not emit turn.ended.
// Keep the idle fallback so reconnects/crashes do not leave sessions stuck busy.
const IDLE_FALLBACK_MS = 2500
const ACTIVE_DELEGATION_STATUSES = new Set<DelegationDto["status"]>(["pending", "running"])

export function useSessionEvents(
  sessionId: MaybeRefOrGetter<string>,
  instanceId: MaybeRefOrGetter<string>,
  onAgentSwitch?: MaybeRefOrGetter<((agent: string) => void) | undefined>,
  suppressAutoScrollRef?: ShallowRef<boolean>,
  enabled: MaybeRefOrGetter<boolean> = true,
): UseSessionEventsResult {
  const sessionsStore = useSessionsStore()
  const messages = ref<AccumulatedMessage[]>([])
  const delegations = ref<DelegationDto[]>([])
  const status = shallowRef<SessionConnectionStatus>("connecting")
  const sessionStatus = shallowRef<SessionStreamStatus>("idle")
  const explicitStatus = shallowRef<SessionStreamExplicitStatus>("idle")
  const error = shallowRef<string | undefined>(undefined)
  const cacheHit = shallowRef(false)
  const initialScrollPosition = shallowRef<{ scrollTop: number; scrollHeight: number } | null>(null)
  const scrollPositionRef = shallowRef<{ scrollTop: number; scrollHeight: number } | null>(null)
  const reconnectAttempt = shallowRef(0)
  const lastEventId = shallowRef<number | null>(null)
  const onAgentSwitchRef = shallowRef<((agent: string) => void) | undefined>(toValue(onAgentSwitch))
  let idleFallbackTimer: ReturnType<typeof setTimeout> | null = null
  let idleFallbackPending = false
  let skipInitialGapFill = true

  const currentSessionId = computed(() => toValue(sessionId))
  const currentInstanceId = computed(() => toValue(instanceId))
  const isEnabled = computed(() => toValue(enabled))
  const pagination = useMessagePagination()
  const { subscribe } = useWeaveSocket()

  const loadCommittedEventsSinceRef = shallowRef<((afterEventId: number | null, signal?: AbortSignal) => Promise<void>) | null>(null)
  const loadDelegationsRef = shallowRef<((signal?: AbortSignal) => Promise<void>) | null>(null)

  watch(
    () => toValue(onAgentSwitch),
    (nextOnAgentSwitch) => {
      onAgentSwitchRef.value = nextOnAgentSwitch
    },
  )

  watch(
    () => [currentSessionId.value, currentInstanceId.value, isEnabled.value] as const,
    ([activeSessionId, activeInstanceId, enabledForSession], _, onCleanup) => {
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

        const orderedEvents = [...events].sort(compareByEventCursor)
        const state: SessionEventState = {
          messages,
          delegations,
          status,
          sessionStatus,
          explicitStatus,
          error,
          onAgentSwitch: onAgentSwitchRef,
          lastEventId,
          scheduleIdleFallback,
          clearIdleFallback,
          maybeResolveIdleFallback,
          syncSessionStore: (patch) => {
            syncSessionStore(activeSessionId, patch)
          },
        }

        for (const committedEvent of orderedEvents) {
          const eventId = getEventIdWithCompatibilityFallback(committedEvent)
          handleEvent(
            {
              type: committedEvent.type,
              eventId,
              properties: committedEvent.payload,
            },
            activeSessionId,
            state,
          )
        }
      }

      // Flag-off mode still relies on the committed-events REST gap-fill because the
      // legacy v1 socket protocol cannot replay missed events after reconnect.
      async function loadCommittedEventsSince(
        afterEventId: number | null,
        loadSignal?: AbortSignal,
      ): Promise<void> {
        if (!activeSessionId || !activeInstanceId) {
          return
        }

        if (afterEventId == null) {
          await loadAllMessages(loadSignal)
          return
        }

        try {
          const params = new URLSearchParams({
            afterEventId: String(afterEventId),
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
            const nextDelegations = Array.isArray(data) ? data : []
            delegations.value = nextDelegations
            syncDerivedSessionStatus(activeSessionId, sessionStatus, explicitStatus, delegations)
            maybeResolveIdleFallback()
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
      explicitStatus.value = "idle"
      sessionStatus.value = "idle"
      status.value = "connecting"
      error.value = undefined
      lastEventId.value = null

      if (suppressAutoScrollRef) {
        suppressAutoScrollRef.value = false
      }

      if (!enabledForSession) {
        status.value = "connected"
        pagination.resetPagination()

        onCleanup(() => {
          disposed = true
          abortController.abort()
          clearIdleFallback()
        })

        return
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
        explicitStatus.value = toExplicitStatus(cached.sessionStatus)
        syncDerivedSessionStatus(activeSessionId, sessionStatus, explicitStatus, delegations)
        lastEventId.value = cached.lastEventId
        pagination.hydratePagination(cached.pagination)
        cacheHit.value = true
        initialScrollPosition.value = {
          scrollTop: cached.scrollPosition,
          scrollHeight: cached.scrollHeight,
        }

        void Promise.all([
          loadCommittedEventsSince(cached.lastEventId, signal),
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
          explicitStatus,
          error,
          onAgentSwitch: onAgentSwitchRef,
          lastEventId,
          scheduleIdleFallback,
          clearIdleFallback,
          maybeResolveIdleFallback,
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
          lastEventId.value = null
          return
        }

        if (operation.session.session.id !== activeSessionId) {
          return
        }

        const incomingActivityStatus = operation.session.activityStatus
        if (incomingActivityStatus === "busy") {
          explicitStatus.value = "busy"
          syncDerivedSessionStatus(activeSessionId, sessionStatus, explicitStatus, delegations)
          scheduleIdleFallback()
          return
        }

        if (incomingActivityStatus === "delegating") {
          clearIdleFallback()
          explicitStatus.value = "delegating"
          syncDerivedSessionStatus(activeSessionId, sessionStatus, explicitStatus, delegations)
          return
        }

        if (incomingActivityStatus === "idle") {
          clearIdleFallback()
          explicitStatus.value = "idle"
          syncDerivedSessionStatus(activeSessionId, sessionStatus, explicitStatus, delegations)
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
          loadCommittedEventsSince(lastEventId.value),
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
            lastEventId: lastEventId.value,
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
    explicitStatus.value = "idle"
    syncDerivedSessionStatus(currentSessionId.value, sessionStatus, explicitStatus, delegations)
  }

  function forceBusy(): void {
    explicitStatus.value = "busy"
    syncDerivedSessionStatus(currentSessionId.value, sessionStatus, explicitStatus, delegations)
    scheduleIdleFallback()
  }

  function syncSessionStore(
    targetSessionId: string | undefined,
    patch: SessionStorePatch,
  ): void {
    if (!targetSessionId) {
      return
    }

    sessionsStore.patchSession(targetSessionId, patch)
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
    currentSessionStatus: SessionStreamStatus,
  ): void {
    const mappedActivityStatus = mapSessionStatusToActivityStatus(currentSessionStatus)

    syncSessionStore(targetSessionId, {
      activityStatus: mappedActivityStatus,
      lifecycleStatus: "running",
      sessionStatus: mappedActivityStatus === "idle" ? "idle" : "active",
    })
  }

  function mapSessionStatusToActivityStatus(status: SessionStreamStatus): "idle" | "busy" | "delegating" {
    if (status === "delegating") {
      return "delegating"
    }

    return status === "idle" ? "idle" : "busy"
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
      idleFallbackPending = false
      return
    }

    clearTimeout(idleFallbackTimer)
    idleFallbackTimer = null
    idleFallbackPending = false
  }

  function scheduleIdleFallback(): void {
    clearIdleFallback()
    idleFallbackPending = true
    idleFallbackTimer = setTimeout(() => {
      idleFallbackTimer = null

      if (hasActiveDelegations(delegations.value)) {
        return
      }

      resolveIdleFallback()
    }, IDLE_FALLBACK_MS)
  }

  function maybeResolveIdleFallback(): void {
    if (!idleFallbackPending || idleFallbackTimer !== null || hasActiveDelegations(delegations.value)) {
      return
    }

    resolveIdleFallback()
  }

  function resolveIdleFallback(): void {
    idleFallbackPending = false
    explicitStatus.value = "idle"
    syncDerivedSessionStatus(currentSessionId.value, sessionStatus, explicitStatus, delegations)
  }

  function reconnect(): void {
    if (!currentSessionId.value || !currentInstanceId.value) {
      return
    }

    status.value = "recovering"

    void Promise.all([
      loadCommittedEventsSinceRef.value?.(lastEventId.value),
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

  const cursorId = getEventCursorId(event)
  if (Number.isFinite(cursorId)) {
    state.lastEventId.value = Math.max(state.lastEventId.value ?? 0, cursorId)
  }

  const delegationId = properties?.delegationId
  const parentToolCallId = properties?.parentToolCallId
  const childSessionId = properties?.childSessionId
  const delegationTitle = properties?.title
  const delegationStatus = properties?.status
  const delegationCreatedAt = properties?.createdAt

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
    syncCapabilitiesForState(properties, state)

    const rawActivityStatus = properties?.activityStatus

    if (rawActivityStatus === "idle") {
      clearIdleFallbackForState(state)
      state.explicitStatus.value = "idle"
      syncDerivedSessionStatusForState(sessionId, state)
    } else if (rawActivityStatus === "busy" || rawActivityStatus === "working") {
      state.explicitStatus.value = "busy"
      syncDerivedSessionStatusForState(sessionId, state)
      scheduleIdleFallbackForState(state)
    } else if (rawActivityStatus === "delegating") {
      clearIdleFallbackForState(state)
      state.explicitStatus.value = "delegating"
      syncDerivedSessionStatusForState(sessionId, state)
    }

    return
  }

  if (type === "session.status" || type === "session-status-changed") {
    syncCapabilitiesForState(properties, state)

    const rawStatus = properties?.status
    const statusType = typeof rawStatus === "string"
      ? rawStatus
      : rawStatus?.type

    if (statusType === "idle") {
      clearIdleFallbackForState(state)
      state.explicitStatus.value = "idle"
      syncDerivedSessionStatusForState(sessionId, state)
    } else if (statusType === "busy" || statusType === "working") {
      state.explicitStatus.value = "busy"
      syncDerivedSessionStatusForState(sessionId, state)
      scheduleIdleFallbackForState(state)
    } else if (statusType === "delegating") {
      clearIdleFallbackForState(state)
      state.explicitStatus.value = "delegating"
      syncDerivedSessionStatusForState(sessionId, state)
    }
    return
  }

  if (type === "session.idle") {
    clearIdleFallbackForState(state)
    state.explicitStatus.value = "idle"
    syncDerivedSessionStatusForState(sessionId, state)
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

  if (type === "user.prompt.committed") {
    const info = properties?.info
    if (!info?.id) {
      return
    }

    confirmSentPrompt(sessionId, {
      correlationId: typeof properties?.correlationId === "string" ? properties.correlationId : undefined,
      eventId: Number.isFinite(cursorId) ? cursorId : undefined,
      serverMessageId: info.id,
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
    syncDerivedSessionStatusForState(sessionId, state)
    maybeResolveIdleFallbackForState(state)
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
    syncDerivedSessionStatusForState(sessionId, state)
    maybeResolveIdleFallbackForState(state)
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

const BOOLEAN_CAPABILITY_KEYS = [
  "canPrompt",
  "canStop",
  "canResume",
  "canRestart",
  "canAbort",
  "canArchive",
  "canUnarchive",
  "canFork",
  "canDelete",
] as const satisfies readonly (keyof SessionActionCapabilities)[]

const DISABLED_REASON_KEYS = [
  "promptDisabledReason",
  "stopDisabledReason",
  "resumeDisabledReason",
  "restartDisabledReason",
  "abortDisabledReason",
  "archiveDisabledReason",
  "unarchiveDisabledReason",
  "forkDisabledReason",
  "deleteDisabledReason",
] as const satisfies readonly (keyof SessionActionCapabilities)[]

function syncCapabilitiesForState(
  properties: WebSocketEvent["properties"] | undefined,
  state: SessionEventState,
): void {
  const capabilities = readSessionActionCapabilities(properties)
  if (!capabilities) {
    return
  }

  state.syncSessionStore?.({ capabilities })
}

function readSessionActionCapabilities(
  properties: WebSocketEvent["properties"] | undefined,
): SessionActionCapabilities | undefined {
  const capabilities = properties?.capabilities
  if (!isSessionActionCapabilities(capabilities)) {
    return undefined
  }

  return capabilities
}

function isSessionActionCapabilities(value: unknown): value is SessionActionCapabilities {
  if (!value || typeof value !== "object") {
    return false
  }

  const candidate = value as Record<keyof SessionActionCapabilities, unknown>
  const hasBooleanCapabilities = BOOLEAN_CAPABILITY_KEYS.every((key) => typeof candidate[key] === "boolean")
  const hasDisabledReasons = DISABLED_REASON_KEYS.every((key) => {
    const disabledReason = candidate[key]
    return disabledReason === null || typeof disabledReason === "string"
  })

  return hasBooleanCapabilities && hasDisabledReasons
}

function compareByEventCursor(
  left: Pick<WebSocketEvent, "eventId" | "sequenceNumber">,
  right: Pick<WebSocketEvent, "eventId" | "sequenceNumber">,
): number {
  const leftCursorId = getEventCursorId(left)
  const rightCursorId = getEventCursorId(right)

  if (!Number.isFinite(leftCursorId) || !Number.isFinite(rightCursorId)) {
    return 0
  }

  return leftCursorId - rightCursorId
}

function getEventCursorId(event: Pick<WebSocketEvent, "eventId" | "sequenceNumber">): number {
  return getEventIdWithCompatibilityFallback(event)
}

function getEventIdWithCompatibilityFallback(event: Pick<WebSocketEvent, "eventId" | "sequenceNumber">): number {
  if (typeof event.eventId === "number") {
    return event.eventId
  }

  if (typeof event.sequenceNumber === "number") {
    return event.sequenceNumber
  }

  return Number.NaN
}

function scheduleIdleFallbackForState(state: SessionEventState): void {
  const scheduler = (state as SessionEventState & { scheduleIdleFallback?: () => void }).scheduleIdleFallback
  scheduler?.()
}

function clearIdleFallbackForState(state: SessionEventState): void {
  const clearer = (state as SessionEventState & { clearIdleFallback?: () => void }).clearIdleFallback
  clearer?.()
}

function maybeResolveIdleFallbackForState(state: SessionEventState): void {
  const resolver = (state as SessionEventState & { maybeResolveIdleFallback?: () => void }).maybeResolveIdleFallback
  resolver?.()
}

function syncDerivedSessionStatusForState(sessionId: string, state: SessionEventState): void {
  syncDerivedSessionStatus(sessionId, state.sessionStatus, state.explicitStatus, state.delegations, state.syncSessionStore)
}

function syncDerivedSessionStatus(
  sessionId: string | undefined,
  sessionStatus: ShallowRef<SessionStreamStatus>,
  explicitStatus: ShallowRef<SessionStreamExplicitStatus>,
  delegations: Ref<DelegationDto[]>,
  syncSessionStorePatch?: (patch: SessionStorePatch) => void,
): void {
  const nextStatus = deriveSessionStatus(explicitStatus.value, delegations.value)
  sessionStatus.value = nextStatus

  if (nextStatus === "idle" && sessionId) {
    clearSentPrompts(sessionId)
    clearPendingPrompts(sessionId)
  }

  syncSessionStorePatch?.({
    activityStatus: mapSessionStatusToActivityStatus(nextStatus),
    lifecycleStatus: "running",
    sessionStatus: nextStatus === "idle" ? "idle" : "active",
  })
}

function deriveSessionStatus(
  explicitStatus: SessionStreamExplicitStatus,
  delegations: DelegationDto[],
): SessionStreamStatus {
  if (explicitStatus === "busy") {
    return "busy"
  }

  if (explicitStatus === "delegating") {
    return "delegating"
  }

  if (hasActiveDelegations(delegations)) {
    return "delegating"
  }

  return "idle"
}

function hasActiveDelegations(delegations: DelegationDto[]): boolean {
  return delegations.some((delegation) => ACTIVE_DELEGATION_STATUSES.has(delegation.status))
}

function toExplicitStatus(status: SessionStreamStatus): SessionStreamExplicitStatus {
  return status
}

function mapSessionStatusToActivityStatus(status: SessionStreamStatus): "idle" | "busy" | "delegating" {
  if (status === "delegating") {
    return "delegating"
  }

  return status === "idle" ? "idle" : "busy"
}
