import {
  computed,
  onMounted,
  onUnmounted,
  readonly,
  shallowRef,
  type MaybeRefOrGetter,
  type ShallowRef,
} from "vue"
import type { AccumulatedMessage, DelegationDto } from "@/lib/api-types"
import type { SessionStreamStatus } from "@/lib/domain-event-reducer"
import { useSessionEvents, type SessionConnectionStatus, type UseSessionEventsResult } from "@/composables/use-session-events"
import { useSessionStream } from "@/composables/use-session-stream"

export const STREAM_PROTOCOL_V2_STORAGE_KEY = "weave_v2_stream"

function readStreamProtocolV2Flag(): boolean {
  if (typeof window === "undefined") {
    return false
  }

  try {
    const value = window.localStorage.getItem(STREAM_PROTOCOL_V2_STORAGE_KEY)
    return value === "1" || value === "true"
  } catch {
    return false
  }
}

export function useStreamProtocolV2Flag(): Readonly<ShallowRef<boolean>> {
  const isEnabled = shallowRef(readStreamProtocolV2Flag())
  let intervalId: number | null = null

  function syncFlag(): void {
    isEnabled.value = readStreamProtocolV2Flag()
  }

  function handleStorage(event: StorageEvent): void {
    if (event.key !== null && event.key !== STREAM_PROTOCOL_V2_STORAGE_KEY) {
      return
    }

    syncFlag()
  }

  onMounted(() => {
    syncFlag()
    window.addEventListener("storage", handleStorage)
    window.addEventListener("focus", syncFlag)
    document.addEventListener("visibilitychange", syncFlag)
    intervalId = window.setInterval(syncFlag, 500)
  })

  onUnmounted(() => {
    window.removeEventListener("storage", handleStorage)
    window.removeEventListener("focus", syncFlag)
    document.removeEventListener("visibilitychange", syncFlag)

    if (intervalId !== null) {
      window.clearInterval(intervalId)
      intervalId = null
    }
  })

  return readonly(isEnabled)
}

export function useSessionEventsSwitch(
  sessionId: MaybeRefOrGetter<string>,
  instanceId: MaybeRefOrGetter<string>,
  onAgentSwitch?: MaybeRefOrGetter<((agent: string) => void) | undefined>,
  suppressAutoScrollRef?: ShallowRef<boolean>,
): UseSessionEventsResult {
  const useStreamProtocolV2 = useStreamProtocolV2Flag()
  const v1 = useSessionEvents(
    sessionId,
    instanceId,
    onAgentSwitch,
    suppressAutoScrollRef,
    computed(() => !useStreamProtocolV2.value),
  )
  const v2 = useSessionStream(sessionId, useStreamProtocolV2)

  const v2Status = computed<SessionConnectionStatus>(() => (v2.isLoading.value ? "connecting" : "connected"))
  const v2Error = shallowRef<string | undefined>(undefined)
  const v2ReconnectAttempt = shallowRef(0)
  const v2TotalMessageCount = shallowRef<number | null>(null)
  const v2LoadOlderError = shallowRef<string | null>(null)
  const v2CacheHit = shallowRef(false)
  const v2InitialScrollPosition = shallowRef<{ scrollTop: number; scrollHeight: number } | null>(null)
  const v2ScrollPositionRef = shallowRef<{ scrollTop: number; scrollHeight: number } | null>(null)

  const messages = computed<readonly AccumulatedMessage[]>(() => useStreamProtocolV2.value ? v2.messages.value : v1.messages.value)
  const delegations = computed<readonly DelegationDto[]>(() => useStreamProtocolV2.value ? v2.delegations.value : v1.delegations.value)
  const status = computed<SessionConnectionStatus>(() => useStreamProtocolV2.value ? v2Status.value : v1.status.value)
  const sessionStatus = computed<SessionStreamStatus>(() => useStreamProtocolV2.value ? v2.sessionStatus.value : v1.sessionStatus.value)
  const error = computed<string | undefined>(() => useStreamProtocolV2.value ? v2Error.value : v1.error.value)
  const reconnectAttempt = computed<number>(() => useStreamProtocolV2.value ? v2ReconnectAttempt.value : v1.reconnectAttempt.value)
  const hasMoreMessages = computed<boolean>(() => useStreamProtocolV2.value ? v2.hasMore.value : v1.hasMoreMessages.value)
  const isLoadingOlder = computed<boolean>(() => useStreamProtocolV2.value ? v2.isLoadingOlder.value : v1.isLoadingOlder.value)
  const totalMessageCount = computed<number | null>(() => useStreamProtocolV2.value ? v2TotalMessageCount.value : v1.totalMessageCount.value)
  const loadOlderError = computed<string | null>(() => useStreamProtocolV2.value ? v2LoadOlderError.value : v1.loadOlderError.value)
  const cacheHit = computed<boolean>(() => useStreamProtocolV2.value ? v2CacheHit.value : v1.cacheHit.value)
  const initialScrollPosition = computed<{ scrollTop: number; scrollHeight: number } | null>(() => {
    return useStreamProtocolV2.value ? v2InitialScrollPosition.value : v1.initialScrollPosition.value
  })

  async function loadOlderMessages(): Promise<void> {
    if (useStreamProtocolV2.value) {
      v2.loadOlder()
      return
    }

    await v1.loadOlderMessages()
  }

  function forceBusy(): void {
    if (useStreamProtocolV2.value) {
      return
    }

    v1.forceBusy()
  }

  function forceIdle(): void {
    v1.forceIdle()
  }

  function reconnect(): void {
    if (useStreamProtocolV2.value) {
      return
    }

    v1.reconnect()
  }

  return {
    messages,
    delegations,
    status,
    sessionStatus,
    error,
    forceBusy,
    forceIdle,
    reconnect,
    reconnectAttempt,
    hasMoreMessages,
    isLoadingOlder,
    loadOlderMessages,
    totalMessageCount,
    loadOlderError,
    cacheHit,
    initialScrollPosition,
    scrollPositionRef: useStreamProtocolV2.value ? v2ScrollPositionRef : v1.scrollPositionRef,
  }
}
