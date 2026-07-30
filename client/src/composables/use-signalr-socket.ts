import { onMounted, onUnmounted } from "vue"
import { HubConnection, HubConnectionBuilder, HubConnectionState } from "@microsoft/signalr"
import type { DomainEvent } from "@/lib/domain-events"
import type { SessionHistoryPage, SessionSnapshot } from "@/lib/session-snapshot"

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

const HUB_PATH = "/hubs/session-events"

const topicListeners = new Map<string, Set<TopicCallback>>()
const topicListenersV2 = new Map<string, Set<TopicV2Callback>>()
const lastSnapshotsV2 = new Map<string, SessionSnapshot>()
const reconnectCallbacks = new Map<string, () => void>()
const disconnectCallbacks = new Map<string, () => void>()

let reconnectCallbackNextId = 0
let disconnectCallbackNextId = 0
let connection: HubConnection | null = null
let subscriberCount = 0
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

async function resubscribeAll(): Promise<void> {
  if (!connection || connection.state !== HubConnectionState.Connected) {
    return
  }

  // Re-subscribe to all active v2 topics (sessions)
  const topicsV2 = Array.from(topicListenersV2.keys()).filter((topic) => (topicListenersV2.get(topic)?.size ?? 0) > 0)
  
  for (const topic of topicsV2) {
    try {
      const sessionId = topic.startsWith("session:") ? topic.slice(8) : topic
      const snapshot = await connection.invoke<SessionSnapshot>("SubscribeToSessionAsync", sessionId)
      dispatchSnapshot(topic, snapshot)
    } catch (error) {
      console.error(`Failed to resubscribe to session ${topic}:`, error)
    }
  }
}

async function connect(): Promise<void> {
  if (connection !== null) {
    return
  }

  if (suspendConnectionsForTesting) {
    return
  }

  const hubConnection = new HubConnectionBuilder()
    .withUrl(HUB_PATH)
    .withAutomaticReconnect([1000, 2000, 5000, 10000])
    .build()

  connection = hubConnection

  // Register event handler for incoming events
  hubConnection.on("Event", (topic: string, eventId: number | null, data: unknown) => {
    // Check if this is a v1 event or v2 event based on listeners
    if (topicListeners.has(topic)) {
      dispatch(topic, data)
    }
    
    if (topicListenersV2.has(topic)) {
      const domainEvent = data as DomainEvent
      dispatchEventV2(topic, eventId === null ? domainEvent : { ...domainEvent, eventId })
    }
  })

  // Handle reconnection
  hubConnection.onreconnected(async () => {
    await resubscribeAll()
    
    for (const callback of reconnectCallbacks.values()) {
      callback()
    }
  })

  // Handle disconnection
  hubConnection.onclose(() => {
    if (connection === hubConnection) {
      connection = null
    }
    
    notifyDisconnected()
  })

  try {
    await hubConnection.start()
  } catch (error) {
    console.error("Failed to start SignalR connection:", error)
    connection = null
  }
}

async function disconnect(): Promise<void> {
  if (connection !== null) {
    try {
      await connection.stop()
    } catch (error) {
      console.error("Error stopping SignalR connection:", error)
    }
    connection = null
  }
}

function hasListenersForTopic(topic: string): boolean {
  return (topicListeners.get(topic)?.size ?? 0) > 0 || (topicListenersV2.get(topic)?.size ?? 0) > 0
}

function addTopicListeners(topics: string[], callback: TopicCallback): Unsubscribe {
  for (const topic of topics) {
    let listeners = topicListeners.get(topic)
    if (!listeners) {
      listeners = new Set<TopicCallback>()
      topicListeners.set(topic, listeners)
    }

    listeners.add(callback)
  }

  return () => {
    for (const topic of topics) {
      const listeners = topicListeners.get(topic)
      if (!listeners) {
        continue
      }

      listeners.delete(callback)
      if (listeners.size === 0 && !hasListenersForTopic(topic)) {
        topicListeners.delete(topic)
      } else if (listeners.size === 0) {
        topicListeners.delete(topic)
      }
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

  // If we already have a snapshot cached, deliver it immediately
  const lastSnapshot = lastSnapshotsV2.get(topic)
  if (lastSnapshot) {
    onSnapshot(lastSnapshot)
  }

  // Subscribe to the session via SignalR
  // The hub expects just the session ID (not the "session:" prefixed topic)
  if (connection?.state === HubConnectionState.Connected) {
    const sessionId = topic.startsWith("session:") ? topic.slice(8) : topic
    connection.invoke<SessionSnapshot>("SubscribeToSessionAsync", sessionId)
      .then((snapshot) => {
        dispatchSnapshot(topic, snapshot)
      })
      .catch((error) => {
        console.error(`Failed to subscribe to session ${topic}:`, error)
      })
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
      
      if (connection?.state === HubConnectionState.Connected) {
        const sessionId = topic.startsWith("session:") ? topic.slice(8) : topic
        connection.invoke("UnsubscribeFromSessionAsync", sessionId)
          .catch((error) => {
            console.error(`Failed to unsubscribe from session ${topic}:`, error)
          })
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
    void connect()
  }
}

function decrementSubscribers(): void {
  subscriberCount = Math.max(0, subscriberCount - 1)

  if (subscriberCount === 0) {
    void disconnect()
  }
}

export function _resetForTesting(): void {
  void disconnect()
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
  return connection !== null
}

export function isWeaveSocketConnected(): boolean {
  return connection?.state === HubConnectionState.Connected
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

function sendV2Message(message: unknown): boolean {
  if (connection?.state === HubConnectionState.Connected) {
    // For SignalR, we don't have a generic send - this would need to be mapped
    // to specific hub methods based on message type
    console.warn("sendV2 not fully implemented for SignalR - message:", message)
    return false
  }

  return false
}

function syncTestApi(): void {
  if (typeof window === "undefined") {
    return
  }

  window.__WEAVE_SOCKET_TEST_API = {
    suspend: () => {
      suspendConnectionsForTesting = true
      void disconnect()
      notifyDisconnected()
    },
    resume: () => {
      suspendConnectionsForTesting = false
      if (subscriberCount > 0) {
        void connect()
      }
    },
    isSuspended: () => suspendConnectionsForTesting,
    hasOpenSocket: () => connection?.state === HubConnectionState.Connected,
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
    sendV2: sendV2Message,
  }
}
