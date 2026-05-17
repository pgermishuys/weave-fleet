import { onMounted, onUnmounted } from "vue"
import { wsUrl } from "@/lib/api-client"
import type { DomainEvent } from "@/lib/domain-events"
import type { HistoryResponse, SessionHistoryPage, SessionSnapshot } from "@/lib/session-snapshot"

export type TopicCallback = (topic: string, data: unknown) => void
export type Unsubscribe = () => void
export type SnapshotCallback = (snapshot: SessionSnapshot) => void
export type DomainEventCallback = (event: DomainEvent) => void
export type HistoryCallback = (page: SessionHistoryPage) => void

interface TopicV2Callback {
  onSnapshot: SnapshotCallback
  onEvent: DomainEventCallback
  onHistory?: HistoryCallback
}

export interface WeaveSocketAPI {
  subscribe: (topics: string[], callback: TopicCallback) => Unsubscribe
  subscribeV2: (topic: string, onSnapshot: SnapshotCallback, onEvent: DomainEventCallback, onHistory?: HistoryCallback) => Unsubscribe
  sendV2: (message: unknown) => boolean
}

interface WeaveSocketTestAPI {
  suspend: () => void
  resume: () => void
  isSuspended: () => boolean
  hasOpenSocket: () => boolean
  hasV2Subscriptions: () => boolean
  hasV2Snapshot: (topic: string) => boolean
  v2SnapshotHasText: (topic: string, text: string) => boolean
}

declare global {
  interface Window {
    __WEAVE_SOCKET_TEST_API?: WeaveSocketTestAPI
  }
}

const WS_PATH = "/ws"
const BASE_DELAY_MS = 1_000
const MAX_DELAY_MS = 30_000

const topicListeners = new Map<string, Set<TopicCallback>>()
const topicListenersV2 = new Map<string, Set<TopicV2Callback>>()
const lastSnapshotsV2 = new Map<string, SessionSnapshot>()
const reconnectCallbacks = new Map<string, () => void>()
const disconnectCallbacks = new Map<string, () => void>()

let reconnectCallbackNextId = 0
let disconnectCallbackNextId = 0
let ws: WebSocket | null = null
let subscriberCount = 0
let reconnectDelay = BASE_DELAY_MS
let reconnectTimer: ReturnType<typeof setTimeout> | null = null
let suspendConnectionsForTesting = false

function dispatch(topic: string, data: unknown): void {
  const callbacks = topicListeners.get(topic)
  if (!callbacks) {
    return
  }

  for (const callback of callbacks) {
    callback(topic, data)
  }
}

function dispatchSnapshot(topic: string, snapshot: SessionSnapshot): void {
  lastSnapshotsV2.set(topic, snapshot)
  const callbacks = topicListenersV2.get(topic)
  if (!callbacks) {
    return
  }

  for (const callback of callbacks) {
    callback.onSnapshot(snapshot)
  }
}

function dispatchEventV2(topic: string, event: DomainEvent): void {
  const callbacks = topicListenersV2.get(topic)
  if (!callbacks) {
    return
  }

  for (const callback of callbacks) {
    callback.onEvent(event)
  }
}

function dispatchHistory(topic: string, page: SessionHistoryPage): void {
  const callbacks = topicListenersV2.get(topic)
  if (!callbacks) {
    return
  }

  for (const callback of callbacks) {
    callback.onHistory?.(page)
  }
}

function notifyDisconnected(): void {
  for (const callback of disconnectCallbacks.values()) {
    callback()
  }
}

function sendJson(message: unknown): boolean {
  if (ws?.readyState === WebSocket.OPEN) {
    ws.send(JSON.stringify(message))
    return true
  }

  return false
}

function resubscribeAll(): void {
  const topics = Array.from(topicListeners.keys()).filter((topic) => (topicListeners.get(topic)?.size ?? 0) > 0)
  if (topics.length > 0) {
    sendJson({ type: "subscribe", topics })
  }

  const topicsV2 = Array.from(topicListenersV2.keys()).filter((topic) => (topicListenersV2.get(topic)?.size ?? 0) > 0)
  if (topicsV2.length > 0) {
    sendJson({ type: "subscribe_v2", topics: topicsV2 })
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

  if (suspendConnectionsForTesting) {
    if (subscriberCount > 0) {
      scheduleReconnect()
    }

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
        return
      }

      if (message.type === "snapshot" && typeof message.topic === "string") {
        dispatchSnapshot(message.topic, message.data as SessionSnapshot)
        return
      }

      if (message.type === "event_v2" && typeof message.topic === "string") {
        dispatchEventV2(message.topic, message.data as DomainEvent)
        return
      }

      if (message.type === "history" && typeof message.topic === "string") {
        dispatchHistory(message.topic, (message as HistoryResponse).data)
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

    notifyDisconnected()

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

function hasListenersForTopic(topic: string): boolean {
  return (topicListeners.get(topic)?.size ?? 0) > 0 || (topicListenersV2.get(topic)?.size ?? 0) > 0
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
      if (listeners.size === 0 && !hasListenersForTopic(topic)) {
        topicListeners.delete(topic)
        topicsToUnsubscribe.push(topic)
      } else if (listeners.size === 0) {
        topicListeners.delete(topic)
      }
    }

    if (topicsToUnsubscribe.length > 0 && ws?.readyState === WebSocket.OPEN) {
      sendJson({ type: "unsubscribe", topics: topicsToUnsubscribe })
    }
  }
}

function addTopicListenerV2(
  topic: string,
  onSnapshot: SnapshotCallback,
  onEvent: DomainEventCallback,
  onHistory?: HistoryCallback,
): Unsubscribe {
  let listeners = topicListenersV2.get(topic)

  if (!listeners) {
    listeners = new Set<TopicV2Callback>()
    topicListenersV2.set(topic, listeners)
  }

  const callback: TopicV2Callback = {
    onSnapshot,
    onEvent,
    onHistory,
  }
  listeners.add(callback)

  const lastSnapshot = lastSnapshotsV2.get(topic)
  if (lastSnapshot) {
    onSnapshot(lastSnapshot)
  }

  if (ws?.readyState === WebSocket.OPEN) {
    sendJson({ type: "subscribe_v2", topics: [topic] })
  }

  return () => {
    const currentListeners = topicListenersV2.get(topic)
    if (!currentListeners) {
      return
    }

      currentListeners.delete(callback)

      if (currentListeners.size === 0 && !hasListenersForTopic(topic)) {
        topicListenersV2.delete(topic)
        lastSnapshotsV2.delete(topic)
        if (ws?.readyState === WebSocket.OPEN) {
          sendJson({ type: "unsubscribe", topics: [topic] })
      }
      return
    }

    if (currentListeners.size === 0) {
      topicListenersV2.delete(topic)
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
  suspendConnectionsForTesting = false
  topicListeners.clear()
  topicListenersV2.clear()
  lastSnapshotsV2.clear()
  reconnectCallbacks.clear()
  disconnectCallbacks.clear()
  syncTestApi()
}

export function _getSubscriberCount(): number {
  return subscriberCount
}

export function _isConnected(): boolean {
  return ws !== null
}

export function isWeaveSocketConnected(): boolean {
  return ws?.readyState === WebSocket.OPEN
}

export function onReconnect(callback: () => void): () => void {
  const id = String(reconnectCallbackNextId++)
  reconnectCallbacks.set(id, callback)

  return () => {
    reconnectCallbacks.delete(id)
  }
}

export function onDisconnect(callback: () => void): () => void {
  const id = String(disconnectCallbackNextId++)
  disconnectCallbacks.set(id, callback)

  return () => {
    disconnectCallbacks.delete(id)
  }
}

const stableSubscribe = (topics: string[], callback: TopicCallback): Unsubscribe =>
  addTopicListeners(topics, callback)

const stableSubscribeV2 = (
  topic: string,
  onSnapshot: SnapshotCallback,
  onEvent: DomainEventCallback,
  onHistory?: HistoryCallback,
): Unsubscribe => addTopicListenerV2(topic, onSnapshot, onEvent, onHistory)

function syncTestApi(): void {
  if (typeof window === "undefined") {
    return
  }

  window.__WEAVE_SOCKET_TEST_API = {
    suspend: () => {
      suspendConnectionsForTesting = true
      disconnect()
      notifyDisconnected()
      if (subscriberCount > 0) {
        scheduleReconnect()
      }
    },
    resume: () => {
      suspendConnectionsForTesting = false
      if (subscriberCount > 0) {
        connect()
      }
    },
    isSuspended: () => suspendConnectionsForTesting,
    hasOpenSocket: () => ws?.readyState === WebSocket.OPEN,
    hasV2Subscriptions: () => topicListenersV2.size > 0,
    hasV2Snapshot: (topic: string) => lastSnapshotsV2.has(topic),
    v2SnapshotHasText: (topic: string, text: string) => snapshotHasText(topic, text),
  }
}

function snapshotHasText(topic: string, text: string): boolean {
  const snapshot = lastSnapshotsV2.get(topic)
  if (!snapshot) {
    return false
  }

  return snapshot.messages.some((message) =>
    message.parts.some((part) => {
      if (part.type !== "text" && part.type !== "reasoning") {
        return false
      }

      return part.text.includes(text)
    }),
  )
}

export function useWeaveSocket(): WeaveSocketAPI {
  onMounted(() => {
    syncTestApi()
    incrementSubscribers()
  })

  onUnmounted(() => {
    decrementSubscribers()
  })

  return {
    subscribe: stableSubscribe,
    subscribeV2: stableSubscribeV2,
    sendV2: sendJson,
  }
}
