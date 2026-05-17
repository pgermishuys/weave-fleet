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
import type { AccumulatedMessage, DelegationDto } from "@/lib/api-types"
import type { DomainEvent } from "@/lib/domain-events"
import { applyDomainEvent, createSessionStreamState, type SessionStreamState } from "@/lib/domain-event-reducer"
import { ensureMessage, mergeMessageUpdate } from "@/lib/event-state"
import type { SessionHistoryPage } from "@/lib/session-snapshot"
import { useWeaveSocket, type Unsubscribe } from "@/composables/use-weave-socket"

export interface UseSessionStreamResult {
  messages: ComputedRef<readonly AccumulatedMessage[]>
  delegations: ComputedRef<readonly DelegationDto[]>
  sessionStatus: ComputedRef<"idle" | "busy">
  isLoading: Readonly<ShallowRef<boolean>>
  hasMore: Readonly<ShallowRef<boolean>>
  isLoadingOlder: Readonly<ShallowRef<boolean>>
  loadOlder: () => void
}

function createEmptyState(): SessionStreamState {
  return {
    messages: [],
    delegations: [],
    sessionStatus: "idle",
    lastSequenceNumber: null,
  }
}

export function useSessionStream(
  sessionId: MaybeRefOrGetter<string>,
  enabled: MaybeRefOrGetter<boolean> = true,
): UseSessionStreamResult {
  const { subscribeV2, sendV2 } = useWeaveSocket()
  const currentSessionId = computed(() => toValue(sessionId))
  const isEnabled = computed(() => toValue(enabled))
  const streamState = shallowRef<SessionStreamState>(createEmptyState())
  const isLoading = shallowRef(true)
  const hasMore = shallowRef(false)
  const cursor = shallowRef<string | null>(null)
  const isLoadingOlder = shallowRef(false)
  const isMounted = shallowRef(false)
  const pendingEvents: DomainEvent[] = []
  let unsubscribe: Unsubscribe | null = null

  const messages = computed<readonly AccumulatedMessage[]>(() => streamState.value.messages)
  const delegations = computed<readonly DelegationDto[]>(() => streamState.value.delegations)
  const sessionStatus = computed<"idle" | "busy">(() => streamState.value.sessionStatus)

  function resetState(loading: boolean): void {
    streamState.value = createEmptyState()
    isLoading.value = loading
    hasMore.value = false
    cursor.value = null
    isLoadingOlder.value = false
  }

  function cleanupSubscription(): void {
    pendingEvents.length = 0
    unsubscribe?.()
    unsubscribe = null
    isLoadingOlder.value = false
  }

  function applyHistoryPage(page: SessionHistoryPage): void {
    let nextMessages = streamState.value.messages

    for (const message of page.messages) {
      nextMessages = mergeMessageUpdate(ensureMessage(nextMessages, message.info), {
        ...message.info,
        time: {
          created: message.info.time.created,
          completed: message.info.time.completed ?? undefined,
        },
        cost: message.info.cost ?? undefined,
        tokens: message.info.tokens ?? undefined,
        parts: message.parts.map((part) => ({ ...part } as Record<string, unknown>)),
      })
    }

    streamState.value = {
      ...streamState.value,
      messages: nextMessages,
    }

    cursor.value = page.cursor
    hasMore.value = page.hasMore
    isLoadingOlder.value = false
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
            nextState = applyDomainEvent(nextState, event)
          }

          streamState.value = nextState
          hasMore.value = snapshot.hasMore
          cursor.value = snapshot.cursor
          isLoading.value = false
        },
        (event) => {
          if (isLoading.value) {
            pendingEvents.push(event)
            return
          }

          streamState.value = applyDomainEvent(streamState.value, event)
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
    loadOlder,
  }
}
