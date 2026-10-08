import { onMounted, onUnmounted } from "vue"
import { HubConnection, HubConnectionBuilder, HubConnectionState } from "@microsoft/signalr"
import { apiUrlOn } from "@/lib/api-client"
import { liveTarget, type MachineTarget } from "@/lib/machine-target"
import type { DomainEvent } from "@/lib/domain-events"
import type { SessionHistoryPage, SessionSnapshot } from "@/lib/session-snapshot"

/**
 * Live events, one hub connection per machine. Every call names the machine it's about (a `MachineTarget`): code
 * working on a session passes its target (`useMachineTarget()`), the interface's own code `liveTarget()`. A
 * machine's connection opens with its first listener and closes with its last.
 */

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

// Global event handlers for non-session-specific events (e.g., activity_status on "sessions" topic)
type GlobalEventHandler = (event: DomainEvent) => void

// SignalR's own automatic reconnect gives up after its last delay. After that, and when the first
// start fails (e.g. Fleet is restarting), keep trying on this schedule while anything still listens.
const RECONNECT_DELAYS_MS = [2000, 5000, 10000, 30000]

let suspendConnectionsForTesting = false

/** The hub connection to one machine, and everything listening on it. */
class MachineHub {
  /** How to reach the machine; the latest caller's, so a new address or token is used from the next connect. */
  machine: MachineTarget

  readonly topicListenersV2 = new Map<string, Set<TopicV2Callback>>()
  // A listener that joins a topic others already hold may reuse its last snapshot only while no event has
  // arrived since it was asked for. Otherwise it gets a fresh one: the Files canvas keeps listening to a session
  // while you're on another, and coming back rebuilt the conversation from the snapshot you first opened it with.
  readonly lastSnapshotsV2 = new Map<string, CachedSnapshot>()
  private readonly topicEventCounts = new Map<string, number>()
  // The latest snapshot request per topic, which listeners that join before it's answered can share.
  private readonly snapshotRequests = new Map<string, SnapshotRequest>()
  readonly reconnectCallbacks = new Set<() => void>()
  readonly disconnectCallbacks = new Set<() => void>()
  // Told as soon as the connection is lost, including while SignalR is still retrying (the phone's "Can't reach" banner).
  readonly connectionLostCallbacks = new Set<() => void>()

  // Per-topic operation queue to ensure subscribe/unsubscribe operations are sequenced
  private readonly topicOperationQueues = new Map<string, Promise<void>>()
  // Per-topic subscription epoch to detect stale snapshots
  private readonly topicSubscriptionEpochs = new Map<string, number>()

  readonly globalEventHandlers = new Map<string, Set<GlobalEventHandler>>()

  connection: HubConnection | null = null
  subscriberCount = 0

  private reconnectTimer: ReturnType<typeof setTimeout> | null = null
  reconnectAttempt = 0

  constructor(machine: MachineTarget) {
    this.machine = machine
  }

  isConnected(): boolean {
    return this.connection?.state === HubConnectionState.Connected
  }

  private eventCountFor(topic: string): number {
    return this.topicEventCounts.get(topic) ?? 0
  }

  /** The topic's last snapshot, if no event has arrived since it was asked for. */
  private currentSnapshot(topic: string): SessionSnapshot | null {
    const cached = this.lastSnapshotsV2.get(topic)
    return cached && cached.eventCount === this.eventCountFor(topic) ? cached.snapshot : null
  }

  /**
   * Subscribes the connection to the topic's session and gives the snapshot to the listeners. Unless `fresh`, it
   * shares a request that hasn't been answered yet when that answer can't miss an event: listeners only hear
   * events from the moment they join.
   */
  private requestSnapshot(topic: string, listeners: Iterable<TopicV2Callback>, fresh = false): Promise<void> {
    if (!this.isConnected()) {
      return Promise.resolve()
    }

    const epoch = this.topicSubscriptionEpochs.get(topic) ?? 0
    const pending = this.snapshotRequests.get(topic)
    if (!fresh && pending && pending.epoch === epoch && (pending.sentAt === null || pending.sentAt === this.eventCountFor(topic))) {
      for (const listener of listeners) {
        pending.waiters.add(listener)
      }
      return Promise.resolve()
    }

    const request: SnapshotRequest = { epoch, sentAt: null, waiters: new Set(listeners) }
    this.snapshotRequests.set(topic, request)
    // The hub expects just the session ID (not the "session:" prefixed topic)
    const sessionId = topic.startsWith("session:") ? topic.slice(8) : topic
    return this.queueTopicOperation(topic, async () => {
      try {
        request.sentAt = this.eventCountFor(topic)
        const snapshot = await this.connection!.invoke<SessionSnapshot>("SubscribeToSessionAsync", sessionId)
        // Only dispatch if this is still the current epoch (not stale)
        if (this.topicSubscriptionEpochs.get(topic) !== epoch) {
          return
        }

        this.lastSnapshotsV2.set(topic, { snapshot, eventCount: request.sentAt })
        const current = this.topicListenersV2.get(topic)
        for (const waiter of request.waiters) {
          if (current?.has(waiter)) {
            waiter.onSnapshot(snapshot)
          }
        }
      } finally {
        if (this.snapshotRequests.get(topic) === request) {
          this.snapshotRequests.delete(topic)
        }
      }
    }).catch((error) => {
      console.error(`Failed to subscribe to session ${topic}:`, error)
    })
  }

  handleHubEvent(topic: string, eventId: number | null, data: unknown): void {
    const domainEvent = toDomainEvent(eventId, data)

    // Dispatch to per-session topic listeners
    const callbacks = this.topicListenersV2.get(topic)
    if (callbacks) {
      this.topicEventCounts.set(topic, this.eventCountFor(topic) + 1)
      for (const callback of callbacks) {
        callback.onEvent(domainEvent)
      }
    }

    // Dispatch to global topic handlers (e.g., "sessions" topic for activity_status)
    for (const handler of this.globalEventHandlers.get(topic) ?? []) {
      handler(domainEvent)
    }
  }

  notifyDisconnected(): void {
    for (const callback of this.disconnectCallbacks) {
      callback()
    }
  }

  private notifyConnectionLost(): void {
    for (const callback of this.connectionLostCallbacks) {
      callback()
    }
  }

  private notifyReconnected(): void {
    for (const callback of this.reconnectCallbacks) {
      callback()
    }
  }

  private async resubscribeAll(): Promise<void> {
    if (!this.isConnected()) {
      return
    }

    // Re-subscribe to all active v2 topics (sessions). Every listener gets a fresh snapshot: events may have
    // been missed while the connection was down.
    const topicsV2 = Array.from(this.topicListenersV2.keys()).filter((topic) => this.hasListenersForTopic(topic))
    await Promise.all(topicsV2.map((topic) => this.requestSnapshot(topic, this.topicListenersV2.get(topic) ?? [], true)))
  }

  async connect(): Promise<void> {
    if (this.connection !== null) {
      return
    }

    if (suspendConnectionsForTesting) {
      return
    }

    // Home takes the cookie; another machine takes its token in place of it. SignalR sends the token as a header
    // where it can and as access_token on the WebSocket, where a browser can't.
    const machine = this.machine.connection
    const hubConnection = new HubConnectionBuilder()
      .withUrl(
        apiUrlOn(machine, HUB_PATH),
        machine ? { accessTokenFactory: () => machine.token, withCredentials: false } : {},
      )
      .withAutomaticReconnect([1000, 2000, 5000, 10000])
      .build()

    this.connection = hubConnection

    // Register event handler for incoming events
    hubConnection.on("Event", (topic: string, eventId: number | null, data: unknown) => this.handleHubEvent(topic, eventId, data))

    hubConnection.onreconnecting(() => this.notifyConnectionLost())

    // Handle reconnection
    hubConnection.onreconnected(async () => {
      await this.resubscribeAll()

      // Re-subscribe to global topics
      await this.subscribeToGlobalTopics()

      this.notifyReconnected()
    })

    // Handle disconnection
    hubConnection.onclose(() => {
      if (this.connection === hubConnection) {
        this.connection = null
      }

      this.notifyDisconnected()
      this.notifyConnectionLost()
      // SignalR has given up (or the connection dropped for good). Without this the app stayed
      // deaf, showing every session as it last was, until a reload.
      this.scheduleReconnect()
    })

    try {
      await hubConnection.start()
      // Subscribe any topics that were registered before the connection was ready
      await this.resubscribeAll()
      // Subscribe to global topics (e.g., "sessions" for activity_status events)
      await this.subscribeToGlobalTopics()
      this.reconnectAttempt = 0
    } catch (error) {
      console.error("Failed to start SignalR connection:", error)
      if (this.connection === hubConnection) {
        this.connection = null
      }
      this.scheduleReconnect()
    }
  }

  wantsConnection(): boolean {
    return this.subscriberCount > 0 && !suspendConnectionsForTesting
  }

  private cancelReconnect(): void {
    if (this.reconnectTimer !== null) {
      clearTimeout(this.reconnectTimer)
      this.reconnectTimer = null
    }
  }

  private scheduleReconnect(): void {
    if (!this.wantsConnection() || this.reconnectTimer !== null) {
      return
    }

    const delay = RECONNECT_DELAYS_MS[Math.min(this.reconnectAttempt, RECONNECT_DELAYS_MS.length - 1)]
    this.reconnectAttempt += 1
    this.reconnectTimer = setTimeout(() => {
      this.reconnectTimer = null
      void this.reconnectNow()
    }, delay)
  }

  /** Starts a new connection after the old one closed, then lets listeners catch up on what they missed. */
  private async reconnectNow(): Promise<void> {
    this.cancelReconnect()
    if (!this.wantsConnection() || this.connection !== null) {
      return
    }

    await this.connect()
    if (!this.isConnected()) {
      return
    }

    // Sessions already got fresh snapshots in connect(); terminals, canvases and progress refresh here.
    this.notifyReconnected()
  }

  // Back online or back on the tab: don't wait out the backoff.
  reconnectIfClosed(): void {
    if (this.connection === null && this.wantsConnection() && (typeof document === "undefined" || document.visibilityState !== "hidden")) {
      void this.reconnectNow()
    }
  }

  private async subscribeToGlobalTopics(): Promise<void> {
    if (!this.isConnected()) {
      return
    }

    try {
      await this.connection!.invoke("SubscribeToSessionsTopicAsync")
    } catch (error) {
      console.error("Failed to subscribe to global sessions topic:", error)
    }
  }

  async disconnect(): Promise<void> {
    this.cancelReconnect()
    this.reconnectAttempt = 0
    if (this.connection !== null) {
      try {
        await this.connection.stop()
      } catch (error) {
        console.error("Error stopping SignalR connection:", error)
      }
      this.connection = null
    }
  }

  private hasListenersForTopic(topic: string): boolean {
    return (this.topicListenersV2.get(topic)?.size ?? 0) > 0
  }

  /**
   * Queues an operation for a topic to ensure subscribe/unsubscribe operations are sequenced.
   * This prevents fire-and-forget unsubscribe from racing with a subsequent subscribe.
   */
  private queueTopicOperation<T>(topic: string, operation: () => Promise<T>): Promise<T> {
    const existingQueue = this.topicOperationQueues.get(topic) ?? Promise.resolve()

    const newQueue = existingQueue
      .then(() => operation())
      .catch((error) => {
        // Log but don't propagate errors to the queue chain
        console.error(`Topic operation failed for ${topic}:`, error)
        throw error
      })

    // Store a void promise to keep the chain alive (errors are swallowed)
    const voidQueue: Promise<void> = newQueue.then(() => {}).catch(() => {})
    this.topicOperationQueues.set(topic, voidQueue)

    return newQueue
  }

  addTopicListenerV2(
    topic: string,
    onSnapshot: SnapshotCallback,
    onEvent: DomainEventCallback,
    onHistory?: HistoryCallback,
  ): Unsubscribe {
    let listeners = this.topicListenersV2.get(topic)
    const isFirstSubscriber = !listeners || listeners.size === 0

    if (!listeners) {
      listeners = new Set<TopicV2Callback>()
      this.topicListenersV2.set(topic, listeners)
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
      currentEpoch = (this.topicSubscriptionEpochs.get(topic) ?? 0) + 1
      this.topicSubscriptionEpochs.set(topic, currentEpoch)
      void this.requestSnapshot(topic, [callback])
    } else {
      // Adding to existing subscription generation
      currentEpoch = this.topicSubscriptionEpochs.get(topic) ?? 1
      // Reuse the last snapshot while it's current. Without a connection, show the last one anyway:
      // reconnecting sends every listener a fresh one.
      const lastSnapshot = this.currentSnapshot(topic) ?? (this.isConnected() ? null : this.lastSnapshotsV2.get(topic)?.snapshot)
      if (lastSnapshot) {
        onSnapshot(lastSnapshot)
      } else {
        void this.requestSnapshot(topic, [callback])
      }
    }

    return () => {
      const currentListeners = this.topicListenersV2.get(topic)
      if (!currentListeners) {
        return
      }

      currentListeners.delete(callback)

      if (currentListeners.size === 0 && !this.hasListenersForTopic(topic)) {
        // Check if the epoch has changed since we subscribed
        // If it has, a new subscribe has already occurred, so skip unsubscribe
        const latestEpoch = this.topicSubscriptionEpochs.get(topic) ?? 0
        if (latestEpoch !== currentEpoch) {
          // A new subscription has occurred; don't unsubscribe
          return
        }

        this.topicListenersV2.delete(topic)
        this.lastSnapshotsV2.delete(topic)
        this.topicEventCounts.delete(topic)

        if (this.isConnected()) {
          const sessionId = topic.startsWith("session:") ? topic.slice(8) : topic
          // Queue the unsubscribe to ensure it happens after any pending subscribe
          this.queueTopicOperation(topic, async () => {
            // Double-check: skip unsubscribe if new listeners were added (immediate resubscribe)
            if (this.hasListenersForTopic(topic)) {
              return
            }
            await this.connection!.invoke("UnsubscribeFromSessionAsync", sessionId)
          }).catch((error) => {
            console.error(`Failed to unsubscribe from session ${topic}:`, error)
          })
        }
        return
      }

      if (currentListeners.size === 0) {
        this.topicListenersV2.delete(topic)
      }
    }
  }

  incrementSubscribers(): void {
    this.subscriberCount += 1

    if (this.subscriberCount === 1) {
      void this.connect()
    }
  }

  decrementSubscribers(): void {
    this.subscriberCount = Math.max(0, this.subscriberCount - 1)

    if (this.subscriberCount === 0) {
      void this.disconnect()
    }
  }

  snapshotHasText(topic: string, text: string): boolean {
    const snapshot = this.lastSnapshotsV2.get(topic)?.snapshot
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
}

/** Each machine's hub, by `MachineTarget.key`. */
const hubs = new Map<string, MachineHub>()

function hubFor(machine: MachineTarget): MachineHub {
  let hub = hubs.get(machine.key)
  if (!hub) {
    hub = new MachineHub(machine)
    hubs.set(machine.key, hub)
  } else {
    hub.machine = machine
  }
  return hub
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

// Mock mode (vite --mode mock) has no hub. Its mock API pushes hub events
// over Vite's dev socket instead; see client/vite-plugin-mock-api.ts.
if (import.meta.hot) {
  import.meta.hot.on("fleet:mock-hub-event", (message: { topic: string; data: unknown }) => {
    hubFor(liveTarget()).handleHubEvent(message.topic, null, message.data)
  })
}

if (typeof window !== "undefined") {
  const reconnectIfClosed = () => {
    for (const hub of hubs.values()) hub.reconnectIfClosed()
  }
  window.addEventListener("online", reconnectIfClosed)
  document.addEventListener("visibilitychange", reconnectIfClosed)
}

export function _resetForTesting(): void {
  for (const hub of hubs.values()) void hub.disconnect()
  hubs.clear()
  suspendConnectionsForTesting = false
  syncTestApi()
}

export function _getSubscriberCount(machine: MachineTarget): number {
  return hubs.get(machine.key)?.subscriberCount ?? 0
}

export function _isConnected(machine: MachineTarget): boolean {
  return (hubs.get(machine.key)?.connection ?? null) !== null
}

/**
 * The machine's connection as it is right now, for a problem report. Reads state the socket already keeps; nothing
 * is recorded for reports ahead of time.
 */
export function describeSocketForReport(machine: MachineTarget): { state: string; topics: number; withSnapshot: number; retrying: number } {
  const hub = hubFor(machine)
  const topics = Array.from(hub.topicListenersV2.entries()).filter(([, listeners]) => listeners.size > 0).map(([topic]) => topic)
  return {
    state: hub.connection?.state ?? "not started",
    topics: topics.length,
    withSnapshot: topics.filter((topic) => hub.lastSnapshotsV2.has(topic)).length,
    retrying: hub.reconnectAttempt,
  }
}

export function isWeaveSocketConnected(machine: MachineTarget): boolean {
  return hubs.get(machine.key)?.isConnected() ?? false
}

/**
 * Tells the machine whether this tab is looking at one of its sessions, so it can write a recap for turns that end
 * while no one is. The server only counts sessions this connection has subscribed to; after a reconnect, say it
 * again once the snapshot is back.
 */
export function setSessionFocus(machine: MachineTarget, sessionId: string, focused: boolean): void {
  const connection = hubs.get(machine.key)?.connection
  if (connection?.state !== HubConnectionState.Connected) return
  void connection.invoke("SetSessionFocusAsync", sessionId, focused).catch((error: unknown) => {
    console.warn(`Failed to report focus for session ${sessionId}:`, error)
  })
}

/**
 * Tells the machine whether this window is on screen and whether it's a computer or a phone, so phones that asked
 * to be quiet at the desk skip pushes while a computer shows Fleet. Sent every 30 s; the server forgets a window
 * after 90 s.
 */
export function setPresence(machine: MachineTarget, visible: boolean, formFactor: "desktop" | "phone"): void {
  const connection = hubs.get(machine.key)?.connection
  if (connection?.state !== HubConnectionState.Connected) return
  void connection.invoke("SetPresenceAsync", visible, formFactor).catch(() => {
    // An older Fleet has no presence; nothing to do.
  })
}

function addCallback(callbacks: Set<() => void>, callback: () => void): () => void {
  // Wrapped, so the same function registered twice is two registrations, each with its own stop.
  const entry = () => callback()
  callbacks.add(entry)
  return () => {
    callbacks.delete(entry)
  }
}

export function onReconnect(machine: MachineTarget, callback: () => void): () => void {
  return addCallback(hubFor(machine).reconnectCallbacks, callback)
}

/** Called when the connection is lost, as soon as SignalR starts retrying (`onDisconnect` waits until it gives up). */
export function onConnectionLost(machine: MachineTarget, callback: () => void): () => void {
  return addCallback(hubFor(machine).connectionLostCallbacks, callback)
}

export function onDisconnect(machine: MachineTarget, callback: () => void): () => void {
  return addCallback(hubFor(machine).disconnectCallbacks, callback)
}

export function onGlobalEvent(machine: MachineTarget, topic: string, handler: GlobalEventHandler): () => void {
  const { globalEventHandlers } = hubFor(machine)
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

/**
 * Loads the page of a session's messages older than `cursor` (the cursor its snapshot or the previous
 * page returned). Null when there's no connection or the request fails; the caller can try again.
 */
export async function loadSessionHistory(machine: MachineTarget, sessionId: string, cursor: string): Promise<SessionHistoryPage | null> {
  const connection = hubs.get(machine.key)?.connection
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

/** The test API speaks for the live machine; suspending holds every machine's connection. */
function syncTestApi(): void {
  if (typeof window === "undefined") {
    return
  }

  const live = () => hubFor(liveTarget())
  window.__WEAVE_SOCKET_TEST_API = {
    suspend: () => {
      suspendConnectionsForTesting = true
      for (const hub of hubs.values()) {
        void hub.disconnect()
        hub.notifyDisconnected()
      }
    },
    resume: () => {
      suspendConnectionsForTesting = false
      for (const hub of hubs.values()) {
        if (hub.subscriberCount > 0) void hub.connect()
      }
    },
    isSuspended: () => suspendConnectionsForTesting,
    hasOpenSocket: () => live().isConnected(),
    hasV2Subscriptions: () => live().topicListenersV2.size > 0,
    hasV2Snapshot: (topic: string) => live().lastSnapshotsV2.has(topic),
    v2SnapshotHasText: (topic: string, text: string) => live().snapshotHasText(topic, text),
  }
}

/** Keeps `machine`'s connection open while the component is mounted, and subscribes topics on it. */
export function useWeaveSocket(machine: MachineTarget): WeaveSocketAPI {
  const hub = hubFor(machine)

  onMounted(() => {
    syncTestApi()
    hub.incrementSubscribers()
  })

  onUnmounted(() => {
    hub.decrementSubscribers()
  })

  return {
    subscribeV2: (topic, onSnapshot, onEvent, onHistory) => hub.addTopicListenerV2(topic, onSnapshot, onEvent, onHistory),
  }
}
