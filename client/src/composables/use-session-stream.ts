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
import { useWeaveSocket, type Unsubscribe } from "@/composables/use-weave-socket"
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
      currentSessionStatus === "disconnected" ||
      currentSessionStatus === "resuming") {
    return currentSessionStatus
  }

  // Map activity status to session status
  switch (activityStatus) {
    case "idle":
      return "idle"
    case "busy":
    case "delegating":
    case "retry":
    case "waiting_input":
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
  const { subscribeV2, sendV2 } = useWeaveSocket()
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
  let unsubscribe: Unsubscribe | null = null

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

  function cleanupSubscription(): void {
    pendingEvents.length = 0
    unsubscribe?.()
    unsubscribe = null
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
    if (!isEnabled.value || !activeSessionId || !hasMore.value || isLoadingOlder.value || cursor.value === null) {
      return
    }

    isLoadingOlder.value = true
    const sent = sendV2({
      type: "load_history",
      topic: `session:${activeSessionId}`,
      cursor: cursor.value,
    })

    if (!sent) {
      isLoadingOlder.value = false
    }
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
          let nextState = createSessionStreamState(snapshot)

          for (const event of pendingEvents.splice(0, pendingEvents.length)) {
            nextState = applyLiveDomainEvent(nextState, event)
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

          streamState.value = applyLiveDomainEvent(streamState.value, event)
        },
        (page) => {
          applyHistoryPage(page)
        },
      )

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
