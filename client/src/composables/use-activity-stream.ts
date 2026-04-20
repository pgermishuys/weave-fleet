import { onMounted, onUnmounted } from "vue"
import { useWeaveSocket, type TopicCallback } from "@/composables/use-weave-socket"

type ActivityCallback = (payload: unknown) => void

export interface ActivityStreamSubscription {
  on: (eventType: string, callback: ActivityCallback) => void
  off: (eventType: string, callback: ActivityCallback) => void
}

const ACTIVITY_TOPIC = "activity"
const eventListeners = new Map<string, Set<ActivityCallback>>()

function dispatchEvent(eventType: string, payload: unknown): void {
  const callbacks = eventListeners.get(eventType)
  if (!callbacks) {
    return
  }

  for (const callback of callbacks) {
    callback(payload)
  }
}

function addEventCallback(eventType: string, callback: ActivityCallback): void {
  let callbacks = eventListeners.get(eventType)
  if (!callbacks) {
    callbacks = new Set<ActivityCallback>()
    eventListeners.set(eventType, callbacks)
  }

  callbacks.add(callback)
}

function removeEventCallback(eventType: string, callback: ActivityCallback): void {
  const callbacks = eventListeners.get(eventType)
  if (!callbacks) {
    return
  }

  callbacks.delete(callback)
  if (callbacks.size === 0) {
    eventListeners.delete(eventType)
  }
}

const stableSubscription: ActivityStreamSubscription = {
  on: addEventCallback,
  off: removeEventCallback,
}

export function _resetListenersForTesting(): void {
  eventListeners.clear()
}

export function useActivityStream(): ActivityStreamSubscription {
  const { subscribe } = useWeaveSocket()

  let unsubscribe: Unsubscribe | null = null

  onMounted(() => {
    const topicCallback: TopicCallback = (_topic: string, data: unknown) => {
      const message = data as { type?: string } | null
      if (!message?.type) {
        return
      }

      dispatchEvent(message.type, data)
    }

    unsubscribe = subscribe([ACTIVITY_TOPIC], topicCallback)
  })

  onUnmounted(() => {
    unsubscribe?.()
    unsubscribe = null
  })

  return stableSubscription
}

type Unsubscribe = () => void
