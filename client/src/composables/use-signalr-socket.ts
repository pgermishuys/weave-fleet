import { onMounted, onUnmounted } from "vue"
import { HubConnection, HubConnectionBuilder, HubConnectionState } from "@microsoft/signalr"
import { apiUrl } from "@/lib/api-client"
import { getActiveMachine } from "@/lib/machines"
import type { DomainEvent } from "@/lib/domain-events"
import type { SessionHistoryPage, SessionSnapshot } from "@/lib/session-snapshot"

export type Unsubscribe = () => void
export type SnapshotCallback = (snapshot: SessionSnapshot) => void
export type DomainEventCallback = (event: DomainEvent) => void
export type HistoryCallback = (page: SessionHistoryPage) => void

interface TopicV2Callback {
  onSnapshot: SnapshotCallback
  onEvent: DomainEventCallback
  onHistory?: HistoryCallback
}

interface CachedSnapshot {
  snapshot: SessionSnapshot
  /** How many events the topic had seen when this snapshot was asked for. */
  eventCount: number
}

interface SnapshotRequest {
  epoch: number
  /** The topic's event count when the request went out; null while it waits its turn in the topic's queue. */
  sentAt: number | null
  /** The listeners this request's snapshot is for. */
  waiters: Set<TopicV2Callback>
}

export interface WeaveSocketAPI {
  subscribeV2: (topic: string, onSnapshot: SnapshotCallback, onEvent: DomainEventCallback, onHistory?: HistoryCallback) => Unsubscribe
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

const topicListenersV2 = new Map<string, Set<TopicV2Callback>>()
// A listener that joins a topic others already hold may reuse its last snapshot only while no event has
// arrived since it was asked for. Otherwise it gets a fresh one: the Files canvas keeps listening to a session
// while you're on another, and coming back rebuilt the conversation from the snapshot you first opened it with.
const lastSnapshotsV2 = new Map<string, CachedSnapshot>()
const topicEventCounts = new Map<string, number>()
// The latest snapshot request per topic, which listeners that join before it's answered can share.
const snapshotRequests = new Map<string, SnapshotRequest>()
const reconnectCallbacks = new Map<string, () => void>()
const disconnectCallbacks = new Map<string, () => void>()

// Per-topic operation queue to ensure subscribe/unsubscribe operations are sequenced
const topicOperationQueues = new Map<string, Promise<void>>()
// Per-topic subscription epoch to detect stale snapshots
const topicSubscriptionEpochs = new Map<string, number>()

// Global event handlers for non-session-specific events (e.g., activity_status on "sessions" topic)
type GlobalEventHandler = (event: DomainEvent) => void
const globalEventHandlers = new Map<string, Set<GlobalEventHandler>>()

let reconnectCallbackNextId = 0
let disconnectCallbackNextId = 0
let connection: HubConnection | null = null
let subscriberCount = 0
let suspendConnectionsForTesting = false

// SignalR's own automatic reconnect gives up after its last delay. After that, and when the first
// start fails (e.g. Fleet is restarting), keep trying on this schedule while anything still listens.
const RECONNECT_DELAYS_MS = [2000, 5000, 10000, 30000]
let reconnectTimer: ReturnType<typeof setTimeout> | null = null
let reconnectAttempt = 0

function eventCountFor(topic: string): number {
  return topicEventCounts.get(topic) ?? 0
}

/** The topic's last snapshot, if no event has arrived since it was asked for. */
function currentSnapshot(topic: string): SessionSnapshot | null {
  const cached = lastSnapshotsV2.get(topic)
  return cached && cached.eventCount === eventCountFor(topic) ? cached.snapshot : null
}

/**
 * Subscribes the connection to the topic's session and gives the snapshot to the listeners. Unless `fresh`, it
 * shares a request that hasn't been answered yet when that answer can't miss an event: listeners only hear
 * events from the moment they join.
 */
function requestSnapshot(topic: string, listeners: Iterable<TopicV2Callback>, fresh = false): Promise<void> {
  if (connection?.state !== HubConnectionState.Connected) {
    return Promise.resolve()
  }

  const epoch = topicSubscriptionEpochs.get(topic) ?? 0
  const pending = snapshotRequests.get(topic)
  if (!fresh && pending && pending.epoch === epoch && (pending.sentAt === null || pending.sentAt === eventCountFor(topic))) {
    for (const listener of listeners) {
      pending.waiters.add(listener)
    }
    return Promise.resolve()
  }

  const request: SnapshotRequest = { epoch, sentAt: null, waiters: new Set(listeners) }
  snapshotRequests.set(topic, request)
  // The hub expects just the session ID (not the "session:" prefixed topic)
  const sessionId = topic.startsWith("session:") ? topic.slice(8) : topic
  return queueTopicOperation(topic, async () => {
    try {
      request.sentAt = eventCountFor(topic)
      const snapshot = await connection!.invoke<SessionSnapshot>("SubscribeToSessionAsync", sessionId)
      // Only dispatch if this is still the current epoch (not stale)
      if (topicSubscriptionEpochs.get(topic) !== epoch) {
        return
      }

      lastSnapshotsV2.set(topic, { snapshot, eventCount: request.sentAt })
      const current = topicListenersV2.get(topic)
      for (const waiter of request.waiters) {
        if (current?.has(waiter)) {
          waiter.onSnapshot(snapshot)
        }
      }
    } finally {
      if (snapshotRequests.get(topic) === request) {
        snapshotRequests.delete(topic)
      }
    }
  }).catch((error) => {
    console.error(`Failed to subscribe to session ${topic}:`, error)
  })
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

function dispatchGlobalEvent(topic: string, event: DomainEvent): void {
  const handlers = globalEventHandlers.get(topic)
  if (!handlers) {
    return
  }

  for (const handler of handlers) {
    handler(event)
  }
}

/** Maps a hub event to a DomainEvent: the wire calls the payload "properties". */
export function toDomainEvent(eventId: number | null, data: unknown): DomainEvent {
  const wireEvent = data as { type: string; eventId?: number | null; properties?: unknown }
  return {
    type: wireEvent.type,
    payload: wireEvent.properties,
    ...(eventId !== null ? { eventId } : {}),
  } as DomainEvent
}

function handleHubEvent(topic: string, eventId: number | null, data: unknown): void {
  const domainEvent = toDomainEvent(eventId, data)

  // Dispatch to per-session topic listeners
  if (topicListenersV2.has(topic)) {
    topicEventCounts.set(topic, (topicEventCounts.get(topic) ?? 0) + 1)
    dispatchEventV2(topic, domainEvent)
  }

  // Dispatch to global topic handlers (e.g., "sessions" topic for activity_status)
  if (globalEventHandlers.has(topic)) {
    dispatchGlobalEvent(topic, domainEvent)
  }
}

// Mock mode (vite --mode mock) has no hub. Its mock API pushes hub events
// over Vite's dev socket instead; see client/vite-plugin-mock-api.ts.
if (import.meta.hot) {
  import.meta.hot.on("fleet:mock-hub-event", (message: { topic: string; data: unknown }) => {
    handleHubEvent(message.topic, null, message.data)
  })
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

  // Re-subscribe to all active v2 topics (sessions). Every listener gets a fresh snapshot: events may have
  // been missed while the connection was down.
  const topicsV2 = Array.from(topicListenersV2.keys()).filter((topic) => (topicListenersV2.get(topic)?.size ?? 0) > 0)
  await Promise.all(topicsV2.map((topic) => requestSnapshot(topic, topicListenersV2.get(topic) ?? [], true)))
}

async function connect(): Promise<void> {
  if (connection !== null) {
    return
  }

  if (suspendConnectionsForTesting) {
    return
  }

  // The hub lives on the machine the app is working in. Another machine takes its token in place of the cookie;
  // SignalR sends it as a header where it can and as access_token on the WebSocket, where a browser can't.
  const machine = getActiveMachine()
  const hubConnection = new HubConnectionBuilder()
    .withUrl(
      apiUrl(HUB_PATH),
      machine ? { accessTokenFactory: () => machine.token, withCredentials: false } : {},
    )
    .withAutomaticReconnect([1000, 2000, 5000, 10000])
    .build()

  connection = hubConnection

  // Register event handler for incoming events
  hubConnection.on("Event", handleHubEvent)

  // Handle reconnection
  hubConnection.onreconnected(async () => {
    await resubscribeAll()
    
    // Re-subscribe to global topics
    await subscribeToGlobalTopics()
    
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
    // SignalR has given up (or the connection dropped for good). Without this the app stayed
    // deaf, showing every session as it last was, until a reload.
    scheduleReconnect()
  })

  try {
    await hubConnection.start()
    // Subscribe any topics that were registered before the connection was ready
    await resubscribeAll()
    // Subscribe to global topics (e.g., "sessions" for activity_status events)
    await subscribeToGlobalTopics()
    reconnectAttempt = 0
  } catch (error) {
    console.error("Failed to start SignalR connection:", error)
    if (connection === hubConnection) {
      connection = null
    }
    scheduleReconnect()
  }
}

function wantsConnection(): boolean {
  return subscriberCount > 0 && !suspendConnectionsForTesting
}

function cancelReconnect(): void {
  if (reconnectTimer !== null) {
    clearTimeout(reconnectTimer)
    reconnectTimer = null
  }
}

function scheduleReconnect(): void {
  if (!wantsConnection() || reconnectTimer !== null) {
    return
  }

  const delay = RECONNECT_DELAYS_MS[Math.min(reconnectAttempt, RECONNECT_DELAYS_MS.length - 1)]
  reconnectAttempt += 1
  reconnectTimer = setTimeout(() => {
    reconnectTimer = null
    void reconnectNow()
  }, delay)
}

/** Starts a new connection after the old one closed, then lets listeners catch up on what they missed. */
async function reconnectNow(): Promise<void> {
  cancelReconnect()
  if (!wantsConnection() || connection !== null) {
    return
  }

  await connect()
  if (!isWeaveSocketConnected()) {
    return
  }

  // Sessions already got fresh snapshots in connect(); terminals, canvases and progress refresh here.
  for (const callback of reconnectCallbacks.values()) {
    callback()
  }
}

// Back online or back on the tab: don't wait out the backoff.
function reconnectIfClosed(): void {
  if (connection === null && wantsConnection() && (typeof document === "undefined" || document.visibilityState !== "hidden")) {
    void reconnectNow()
  }
}

if (typeof window !== "undefined") {
  window.addEventListener("online", reconnectIfClosed)
  document.addEventListener("visibilitychange", reconnectIfClosed)
}

async function subscribeToGlobalTopics(): Promise<void> {
  if (!connection || connection.state !== HubConnectionState.Connected) {
    return
  }

  try {
    await connection.invoke("SubscribeToSessionsTopicAsync")
  } catch (error) {
    console.error("Failed to subscribe to global sessions topic:", error)
  }
}

async function disconnect(): Promise<void> {
  cancelReconnect()
  reconnectAttempt = 0
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
  return (topicListenersV2.get(topic)?.size ?? 0) > 0
}

/**
 * Queues an operation for a topic to ensure subscribe/unsubscribe operations are sequenced.
 * This prevents fire-and-forget unsubscribe from racing with a subsequent subscribe.
 */
function queueTopicOperation<T>(topic: string, operation: () => Promise<T>): Promise<T> {
  const existingQueue = topicOperationQueues.get(topic) ?? Promise.resolve()
  
  const newQueue = existingQueue
    .then(() => operation())
    .catch((error) => {
      // Log but don't propagate errors to the queue chain
      console.error(`Topic operation failed for ${topic}:`, error)
      throw error
    })
  
  // Store a void promise to keep the chain alive (errors are swallowed)
  const voidQueue: Promise<void> = newQueue.then(() => {}).catch(() => {})
  topicOperationQueues.set(topic, voidQueue)
  
  return newQueue
}

function addTopicListenerV2(
  topic: string,
  onSnapshot: SnapshotCallback,
  onEvent: DomainEventCallback,
  onHistory?: HistoryCallback,
): Unsubscribe {
  let listeners = topicListenersV2.get(topic)
  const isFirstSubscriber = !listeners || listeners.size === 0

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

  // Only increment epoch when starting a NEW subscription generation (first subscriber)
  // This prevents multiple concurrent subscribers from invalidating each other
  let currentEpoch: number
  if (isFirstSubscriber) {
    currentEpoch = (topicSubscriptionEpochs.get(topic) ?? 0) + 1
    topicSubscriptionEpochs.set(topic, currentEpoch)
    void requestSnapshot(topic, [callback])
  } else {
    // Adding to existing subscription generation
    currentEpoch = topicSubscriptionEpochs.get(topic) ?? 1
    // Reuse the last snapshot while it's current. Without a connection, show the last one anyway:
    // reconnecting sends every listener a fresh one.
    const lastSnapshot = currentSnapshot(topic) ?? (isWeaveSocketConnected() ? null : lastSnapshotsV2.get(topic)?.snapshot)
    if (lastSnapshot) {
      onSnapshot(lastSnapshot)
    } else {
      void requestSnapshot(topic, [callback])
    }
  }

  return () => {
    const currentListeners = topicListenersV2.get(topic)
    if (!currentListeners) {
      return
    }

    currentListeners.delete(callback)

    if (currentListeners.size === 0 && !hasListenersForTopic(topic)) {
      // Check if the epoch has changed since we subscribed
      // If it has, a new subscribe has already occurred, so skip unsubscribe
      const latestEpoch = topicSubscriptionEpochs.get(topic) ?? 0
      if (latestEpoch !== currentEpoch) {
        // A new subscription has occurred; don't unsubscribe
        return
      }

      topicListenersV2.delete(topic)
      lastSnapshotsV2.delete(topic)
      topicEventCounts.delete(topic)

      if (connection?.state === HubConnectionState.Connected) {
        const sessionId = topic.startsWith("session:") ? topic.slice(8) : topic
        // Queue the unsubscribe to ensure it happens after any pending subscribe
        queueTopicOperation(topic, async () => {
          // Double-check: skip unsubscribe if new listeners were added (immediate resubscribe)
          if (topicListenersV2.has(topic) && (topicListenersV2.get(topic)?.size ?? 0) > 0) {
            return
          }
          await connection!.invoke("UnsubscribeFromSessionAsync", sessionId)
        }).catch((error) => {
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
  cancelReconnect()
  reconnectAttempt = 0
  subscriberCount = 0
  suspendConnectionsForTesting = false
  topicListenersV2.clear()
  lastSnapshotsV2.clear()
  topicEventCounts.clear()
  snapshotRequests.clear()
  reconnectCallbacks.clear()
  disconnectCallbacks.clear()
  topicOperationQueues.clear()
  topicSubscriptionEpochs.clear()
  globalEventHandlers.clear()
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

/**
 * Tells Fleet whether this tab is looking at a session, so it can write a
 * recap for turns that end while no one is. The server only counts sessions
 * this connection has subscribed to; after a reconnect, say it again once the
 * snapshot is back.
 */
export function setSessionFocus(sessionId: string, focused: boolean): void {
  if (connection?.state !== HubConnectionState.Connected) return
  void connection.invoke("SetSessionFocusAsync", sessionId, focused).catch((error: unknown) => {
    console.warn(`Failed to report focus for session ${sessionId}:`, error)
  })
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

export function onGlobalEvent(topic: string, handler: GlobalEventHandler): () => void {
  let handlers = globalEventHandlers.get(topic)
  if (!handlers) {
    handlers = new Set<GlobalEventHandler>()
    globalEventHandlers.set(topic, handlers)
  }

  handlers.add(handler)

  return () => {
    const currentHandlers = globalEventHandlers.get(topic)
    if (!currentHandlers) {
      return
    }

    currentHandlers.delete(handler)

    if (currentHandlers.size === 0) {
      globalEventHandlers.delete(topic)
    }
  }
}

const stableSubscribeV2 = (
  topic: string,
  onSnapshot: SnapshotCallback,
  onEvent: DomainEventCallback,
  onHistory?: HistoryCallback,
): Unsubscribe => addTopicListenerV2(topic, onSnapshot, onEvent, onHistory)

/**
 * Loads the page of a session's messages older than `cursor` (the cursor its snapshot or the previous
 * page returned). Null when there's no connection or the request fails; the caller can try again.
 */
export async function loadSessionHistory(sessionId: string, cursor: string): Promise<SessionHistoryPage | null> {
  if (connection?.state !== HubConnectionState.Connected) {
    return null
  }

  try {
    return await connection.invoke<SessionHistoryPage>("LoadHistoryAsync", sessionId, cursor)
  } catch (error) {
    console.error(`Failed to load older messages for session ${sessionId}:`, error)
    return null
  }
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
  const snapshot = lastSnapshotsV2.get(topic)?.snapshot
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
    subscribeV2: stableSubscribeV2,
  }
}
