import {
  computed,
  onMounted,
  onUnmounted,
  readonly,
  shallowRef,
  toValue,
  watch,
  type ComputedRef,
  type MaybeRefOrGetter,
  type ShallowRef,
} from "vue"
import type { AccumulatedMessage, DelegationDto } from "@/lib/client-types"
import type { DomainEvent } from "@/lib/domain-events"
import {
  applyDomainEvent,
  createSessionStreamState,
  type SessionStreamState,
  type SessionStreamStatus,
} from "@/lib/domain-event-reducer"
import { prependHistoryPage } from "@/lib/history-merge"
import type { SessionHistoryPage } from "@/lib/session-snapshot"
import { loadSessionHistory, useWeaveSocket, type Unsubscribe } from "@/composables/use-weave-socket"
import { onGlobalEvent } from "@/composables/use-signalr-socket"
import { useSessionsStore } from "@/stores/sessions"

export interface UseSessionStreamResult {
  messages: ComputedRef<readonly AccumulatedMessage[]>
  delegations: ComputedRef<readonly DelegationDto[]>
  sessionStatus: ComputedRef<SessionStreamStatus>
  isLoading: Readonly<ShallowRef<boolean>>
  hasMore: Readonly<ShallowRef<boolean>>
  isLoadingOlder: Readonly<ShallowRef<boolean>>
  isPartial: Readonly<ShallowRef<boolean>>
  loadOlder: () => void
}

/** How long live events wait when no frame comes, as in a hidden tab. */
const FRAME_FALLBACK_MS = 100

function createEmptyState(): SessionStreamState {
  return {
    messages: [],
    delegations: [],
    explicitStatus: "idle",
    sessionStatus: "idle",
    lastEventId: null,
  }
}

/**
 * Maps activityStatus to sessionStatus following the server's DeriveSessionStatus logic.
 * Only maps activity-driven states; lifecycle states like "stopped", "completed", "error", "disconnected"
 * are preserved and not clobbered by activity events.
 */
function deriveSessionStatus(activityStatus: string, currentSessionStatus?: string): string {
  // Preserve lifecycle states — don't clobber them with activity-driven states
  if (currentSessionStatus === "stopped" || 
      currentSessionStatus === "completed" || 
      currentSessionStatus === "error" || 
      currentSessionStatus === "disconnected") {
    return currentSessionStatus
  }

  // Map activity status to session status
  switch (activityStatus) {
    case "idle":
      return "idle"
    // A session stopped on a question needs the user, so it gets its own status rather than "active".
    case "waiting_input":
      return "waiting_input"
    case "busy":
    case "delegating":
    case "retry":
      return "active"
    default:
      // Unknown activity status — default to active to be safe
      return "active"
  }
}

export function useSessionStream(
  sessionId: MaybeRefOrGetter<string>,
  enabled: MaybeRefOrGetter<boolean> = true,
): UseSessionStreamResult {
  const { subscribeV2 } = useWeaveSocket()
  const sessionsStore = useSessionsStore()
  const currentSessionId = computed(() => toValue(sessionId))
  const isEnabled = computed(() => toValue(enabled))
  const streamState = shallowRef<SessionStreamState>(createEmptyState())
  const isLoading = shallowRef(true)
  const hasMore = shallowRef(false)
  const cursor = shallowRef<string | null>(null)
  const isLoadingOlder = shallowRef(false)
  const isPartial = shallowRef(false)
  const isMounted = shallowRef(false)
  const pendingEvents: DomainEvent[] = []
  // Sessions-topic events carry no cursor of this session's, so they're kept apart from its own.
  const pendingActivity: DomainEvent[] = []
  let unsubscribe: Unsubscribe | null = null
  let unsubscribeActivity: Unsubscribe | null = null
  // Live events wait here for the next frame. A streamed reply sends a delta per token, each in its own socket
  // message; applying them one by one re-rendered the conversation for every token.
  const frameEvents: Array<{ event: DomainEvent; live: boolean }> = []
  let frameRequest: number | null = null
  let frameFallback: ReturnType<typeof setTimeout> | null = null

  const messages = computed<readonly AccumulatedMessage[]>(() => streamState.value.messages)
  const delegations = computed<readonly DelegationDto[]>(() => streamState.value.delegations)
  const sessionStatus = computed<SessionStreamStatus>(() => streamState.value.sessionStatus)

  function resetState(loading: boolean): void {
    streamState.value = createEmptyState()
    isLoading.value = loading
    hasMore.value = false
    cursor.value = null
    isLoadingOlder.value = false
    isPartial.value = false
  }

  function cancelFrame(): void {
    frameEvents.length = 0
    if (frameRequest !== null) {
      cancelAnimationFrame(frameRequest)
      frameRequest = null
    }
    if (frameFallback !== null) {
      clearTimeout(frameFallback)
      frameFallback = null
    }
  }

  function flushFrame(): void {
    const events = frameEvents.splice(0, frameEvents.length)
    cancelFrame()
    let nextState = streamState.value
    for (const { event, live } of events) {
      nextState = live ? applyLiveDomainEvent(nextState, event) : applyDomainEvent(nextState, event)
    }
    if (nextState !== streamState.value) {
      streamState.value = nextState
    }
  }

  function applyNextFrame(event: DomainEvent, live: boolean): void {
    frameEvents.push({ event, live })
    if (frameRequest !== null) {
      return
    }
    frameRequest = requestAnimationFrame(flushFrame)
    // A hidden tab gets no frames; the conversation still has to be current when it comes back.
    frameFallback = setTimeout(flushFrame, FRAME_FALLBACK_MS)
  }

  function cleanupSubscription(): void {
    cancelFrame()
    pendingEvents.length = 0
    pendingActivity.length = 0
    unsubscribe?.()
    unsubscribe = null
    unsubscribeActivity?.()
    unsubscribeActivity = null
    isLoadingOlder.value = false
  }

  function applyHistoryPage(page: SessionHistoryPage): void {
    streamState.value = {
      ...streamState.value,
      messages: prependHistoryPage(streamState.value.messages, page.messages),
    }

    cursor.value = page.cursor
    hasMore.value = page.hasMore
    isLoadingOlder.value = false
  }

  function applyLiveDomainEvent(state: SessionStreamState, event: DomainEvent): SessionStreamState {
    const nextState = applyDomainEvent(state, event)
    return {
      ...nextState,
      lastEventId: getDomainEventCursor(event) ?? nextState.lastEventId,
    }
  }

  function loadOlder(): void {
    const activeSessionId = currentSessionId.value
    const requestedCursor = cursor.value
    if (!isEnabled.value || !activeSessionId || !hasMore.value || isLoadingOlder.value || requestedCursor === null) {
      return
    }

    isLoadingOlder.value = true
    void loadSessionHistory(activeSessionId, requestedCursor).then((page) => {
      // Leaving the session already reset the stream.
      if (currentSessionId.value !== activeSessionId) {
        return
      }

      // A page a newer snapshot (e.g. after a reconnect) has replaced no longer fits.
      if (page && cursor.value === requestedCursor) {
        applyHistoryPage(page)
        return
      }

      isLoadingOlder.value = false
    })
  }

  watch(
    () => [currentSessionId.value, isEnabled.value, isMounted.value] as const,
    ([activeSessionId, enabledForSession, mounted], _, onCleanup) => {
      cleanupSubscription()

      if (!mounted) {
        resetState(false)
        return
      }

      if (!enabledForSession || !activeSessionId) {
        resetState(false)
        return
      }

      resetState(true)

      const topic = `session:${activeSessionId}`
      unsubscribe = subscribeV2(
        topic,
        (snapshot) => {
          // The snapshot replaces the state those events would have changed.
          cancelFrame()
          let nextState = createSessionStreamState(snapshot)

          for (const event of pendingEvents.splice(0, pendingEvents.length)) {
            nextState = applyLiveDomainEvent(nextState, event)
          }
          for (const event of pendingActivity.splice(0, pendingActivity.length)) {
            nextState = applyDomainEvent(nextState, event)
          }

          streamState.value = nextState
          hasMore.value = snapshot.hasMore
          cursor.value = snapshot.cursor
          isPartial.value = snapshot.isPartial
          isLoading.value = false

          // Sync activity status from snapshot to sessions store
          const currentSession = sessionsStore.sessions.find(
            (s) => s.session.id === activeSessionId
          )
          const newSessionStatus = deriveSessionStatus(
            snapshot.activityStatus,
            currentSession?.sessionStatus
          )
          sessionsStore.patchSession(activeSessionId, {
            activityStatus: snapshot.activityStatus,
            sessionStatus: newSessionStatus,
          })
          sessionsStore.clearSessionStateOverride(activeSessionId)
        },
        (event) => {
          if (isLoading.value) {
            pendingEvents.push(event)
            return
          }

          applyNextFrame(event, true)
        },
        (page) => {
          applyHistoryPage(page)
        },
      )

      // A sub-agent's status comes on the sessions topic like every session's. The reducer keeps its
      // delegations' own and leaves every other session's alone.
      unsubscribeActivity = onGlobalEvent("sessions", (event) => {
        if (event.type !== "activity_status") {
          return
        }

        if (isLoading.value) {
          pendingActivity.push(event)
          return
        }

        applyNextFrame(event, false)
      })

      onCleanup(() => {
        cleanupSubscription()
      })
    },
    { immediate: true },
  )

  onMounted(() => {
    isMounted.value = true
  })

  onUnmounted(() => {
    cleanupSubscription()
  })

  return {
    messages,
    delegations,
    sessionStatus,
    isLoading: readonly(isLoading),
    hasMore: readonly(hasMore),
    isLoadingOlder: readonly(isLoadingOlder),
    isPartial: readonly(isPartial),
    loadOlder,
  }
}

function getDomainEventCursor(event: DomainEvent): number | null {
  return typeof event.eventId === "number" ? event.eventId : null
}
