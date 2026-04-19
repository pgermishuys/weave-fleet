import { onMounted, onUnmounted } from "vue"
import { wsUrl } from "@/lib/api-client"

export type TopicCallback = (topic: string, data: unknown) => void
export type Unsubscribe = () => void

export interface WeaveSocketAPI {
  subscribe: (topics: string[], callback: TopicCallback) => Unsubscribe
}

const WS_PATH = "/ws"
const BASE_DELAY_MS = 1_000
const MAX_DELAY_MS = 30_000

const topicListeners = new Map<string, Set<TopicCallback>>()
const reconnectCallbacks = new Map<string, () => void>()

let reconnectCallbackNextId = 0
let ws: WebSocket | null = null
let subscriberCount = 0
let reconnectDelay = BASE_DELAY_MS
let reconnectTimer: ReturnType<typeof setTimeout> | null = null

function dispatch(topic: string, data: unknown): void {
  const callbacks = topicListeners.get(topic)
  if (!callbacks) {
    return
  }

  for (const callback of callbacks) {
    callback(topic, data)
  }
}

function sendJson(message: unknown): void {
  if (ws?.readyState === WebSocket.OPEN) {
    ws.send(JSON.stringify(message))
  }
}

function resubscribeAll(): void {
  const topics = Array.from(topicListeners.keys()).filter((topic) => (topicListeners.get(topic)?.size ?? 0) > 0)
  if (topics.length > 0) {
    sendJson({ type: "subscribe", topics })
  }
}

function scheduleReconnect(): void {
  if (reconnectTimer !== null) {
    return
  }

  const delay = reconnectDelay + Math.random() * 500
  reconnectDelay = Math.min(reconnectDelay * 2, MAX_DELAY_MS)

  reconnectTimer = setTimeout(() => {
    reconnectTimer = null

    if (subscriberCount > 0) {
      connect()
    }
  }, delay)
}

function connect(): void {
  if (ws !== null || typeof WebSocket === "undefined") {
    return
  }

  const socket = new WebSocket(wsUrl(WS_PATH))
  ws = socket

  socket.onopen = () => {
    reconnectDelay = BASE_DELAY_MS
    resubscribeAll()

    for (const callback of reconnectCallbacks.values()) {
      callback()
    }
  }

  socket.onmessage = (event: MessageEvent<string>) => {
    try {
      const message = JSON.parse(event.data) as {
        type?: string
        topic?: string
        data?: unknown
      }

      if (message.type === "event" && typeof message.topic === "string") {
        dispatch(message.topic, message.data ?? null)
      }
    } catch {
      // Ignore invalid frames.
    }
  }

  socket.onerror = () => {
    // onclose handles reconnect scheduling.
  }

  socket.onclose = () => {
    if (ws === socket) {
      ws = null
    }

    if (subscriberCount > 0) {
      scheduleReconnect()
    }
  }
}

function disconnect(): void {
  if (reconnectTimer !== null) {
    clearTimeout(reconnectTimer)
    reconnectTimer = null
  }

  if (ws !== null) {
    ws.close()
    ws = null
  }

  reconnectDelay = BASE_DELAY_MS
}

function addTopicListeners(topics: string[], callback: TopicCallback): Unsubscribe {
  const topicsToSubscribe: string[] = []

  for (const topic of topics) {
    let listeners = topicListeners.get(topic)
    if (!listeners) {
      listeners = new Set<TopicCallback>()
      topicListeners.set(topic, listeners)
      topicsToSubscribe.push(topic)
    }

    listeners.add(callback)
  }

  if (topicsToSubscribe.length > 0 && ws?.readyState === WebSocket.OPEN) {
    sendJson({ type: "subscribe", topics: topicsToSubscribe })
  }

  return () => {
    const topicsToUnsubscribe: string[] = []

    for (const topic of topics) {
      const listeners = topicListeners.get(topic)
      if (!listeners) {
        continue
      }

      listeners.delete(callback)
      if (listeners.size === 0) {
        topicListeners.delete(topic)
        topicsToUnsubscribe.push(topic)
      }
    }

    if (topicsToUnsubscribe.length > 0 && ws?.readyState === WebSocket.OPEN) {
      sendJson({ type: "unsubscribe", topics: topicsToUnsubscribe })
    }
  }
}

function incrementSubscribers(): void {
  subscriberCount += 1

  if (subscriberCount === 1) {
    connect()
  }
}

function decrementSubscribers(): void {
  subscriberCount = Math.max(0, subscriberCount - 1)

  if (subscriberCount === 0) {
    disconnect()
  }
}

export function _resetForTesting(): void {
  disconnect()
  subscriberCount = 0
  topicListeners.clear()
  reconnectCallbacks.clear()
}

export function _getSubscriberCount(): number {
  return subscriberCount
}

export function _isConnected(): boolean {
  return ws !== null
}

export function onReconnect(callback: () => void): () => void {
  const id = String(reconnectCallbackNextId++)
  reconnectCallbacks.set(id, callback)

  return () => {
    reconnectCallbacks.delete(id)
  }
}

const stableSubscribe = (topics: string[], callback: TopicCallback): Unsubscribe =>
  addTopicListeners(topics, callback)

export function useWeaveSocket(): WeaveSocketAPI {
  onMounted(() => {
    incrementSubscribers()
  })

  onUnmounted(() => {
    decrementSubscribers()
  })

  return {
    subscribe: stableSubscribe,
  }
}
