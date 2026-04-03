/**
 * Singleton WebSocket connection with topic-based subscriptions.
 *
 * Module-level singleton pattern with topic-based subscriptions.
 * a single WebSocket instead of multiple EventSource connections. All
 * per-session events and global activity events flow through one connection.
 *
 * Protocol (mirrors go/internal/ws/protocol.go):
 *
 *   Client → Server:
 *     { "type": "subscribe",   "topics": ["session:abc", "activity"] }
 *     { "type": "unsubscribe", "topics": ["session:abc"] }
 *
 *   Server → Client:
 *     { "type": "event",       "topic": "session:abc", "data": { ... } }
 *     { "type": "subscribed",  "topics": ["session:abc", "activity"] }
 *
 * Usage from hooks:
 *
 *   const { subscribe } = useWeaveSocket();
 *
 *   useEffect(() => {
 *     return subscribe(["session:abc"], (topic, data) => { ... });
 *   }, [subscribe]);
 */

import { useEffect, useCallback } from "react";
import { wsUrl } from "@/lib/api-client";

// ─── Types ───────────────────────────────────────────────────────────────────

/** Callback invoked when an event arrives on a subscribed topic. */
export type TopicCallback = (topic: string, data: unknown) => void;

/** Returned by subscribe(); call to remove the subscription. */
export type Unsubscribe = () => void;

// ─── Module-level singleton ──────────────────────────────────────────────────

const WS_PATH = "/ws";
const BASE_DELAY_MS = 1_000;
const MAX_DELAY_MS = 30_000;

/** topic → set of callbacks registered for that topic */
const topicListeners = new Map<string, Set<TopicCallback>>();

/** id → reconnect callback registered via onReconnect() */
const reconnectCallbacks = new Map<string, () => void>();
let reconnectCallbackNextId = 0;

let ws: WebSocket | null = null;
let subscriberCount = 0;
let reconnectDelay = BASE_DELAY_MS;
let reconnectTimer: ReturnType<typeof setTimeout> | null = null;

// ─── Internal helpers ────────────────────────────────────────────────────────

function dispatch(topic: string, data: unknown): void {
  const callbacks = topicListeners.get(topic);
  if (!callbacks) return;
  for (const cb of callbacks) {
    cb(topic, data);
  }
}

function sendJson(msg: unknown): void {
  if (ws && ws.readyState === WebSocket.OPEN) {
    ws.send(JSON.stringify(msg));
  }
}

function resubscribeAll(): void {
  const topics = Array.from(topicListeners.keys()).filter((t) => topicListeners.get(t)!.size > 0);
  if (topics.length > 0) {
    sendJson({ type: "subscribe", topics });
  }
}

function scheduleReconnect(): void {
  if (reconnectTimer !== null) return; // already scheduled
  const delay = reconnectDelay + Math.random() * 500; // jitter
  reconnectDelay = Math.min(reconnectDelay * 2, MAX_DELAY_MS);
  reconnectTimer = setTimeout(() => {
    reconnectTimer = null;
    if (subscriberCount > 0) {
      connect();
    }
  }, delay);
}

function connect(): void {
  if (ws !== null) return; // already connecting/connected
  if (typeof WebSocket === "undefined") return; // SSR guard

  const url = wsUrl(WS_PATH);
  const socket = new WebSocket(url);
  ws = socket;

  socket.onopen = () => {
    reconnectDelay = BASE_DELAY_MS; // reset backoff
    resubscribeAll();
    // Notify listeners that the socket reconnected so they can gap-fill.
    for (const cb of reconnectCallbacks.values()) {
      cb();
    }
  };

  socket.onmessage = (e: MessageEvent<string>) => {
    try {
      const msg = JSON.parse(e.data) as {
        type: string;
        topic?: string;
        data?: unknown;
        topics?: string[];
      };
      if (msg.type === "event" && msg.topic !== undefined) {
        dispatch(msg.topic, msg.data ?? null);
      }
      // "subscribed" confirmation — no-op for now
    } catch {
      // ignore parse errors
    }
  };

  socket.onerror = () => {
    // onerror is always followed by onclose — handle reconnect there
  };

  socket.onclose = () => {
    if (ws === socket) {
      ws = null;
    }
    if (subscriberCount > 0) {
      scheduleReconnect();
    }
  };
}

function disconnect(): void {
  if (reconnectTimer !== null) {
    clearTimeout(reconnectTimer);
    reconnectTimer = null;
  }
  if (ws) {
    ws.close();
    ws = null;
  }
  reconnectDelay = BASE_DELAY_MS;
}

// ─── Subscription management ─────────────────────────────────────────────────

/**
 * Register callbacks for one or more topics.
 * Returns an unsubscribe function that removes the callbacks and, if no
 * longer needed, sends an unsubscribe message to the server.
 */
function addTopicListeners(topics: string[], callback: TopicCallback): Unsubscribe {
  const topicsToSubscribe: string[] = [];

  for (const topic of topics) {
    let set = topicListeners.get(topic);
    if (!set) {
      set = new Set();
      topicListeners.set(topic, set);
      topicsToSubscribe.push(topic); // newly tracked topic
    }
    set.add(callback);
  }

  // Tell the server about any newly tracked topics
  if (topicsToSubscribe.length > 0 && ws?.readyState === WebSocket.OPEN) {
    sendJson({ type: "subscribe", topics: topicsToSubscribe });
  }

  return () => {
    const topicsToUnsubscribe: string[] = [];

    for (const topic of topics) {
      const set = topicListeners.get(topic);
      if (!set) continue;
      set.delete(callback);
      if (set.size === 0) {
        topicListeners.delete(topic);
        topicsToUnsubscribe.push(topic); // no more listeners for this topic
      }
    }

    if (topicsToUnsubscribe.length > 0 && ws?.readyState === WebSocket.OPEN) {
      sendJson({ type: "unsubscribe", topics: topicsToUnsubscribe });
    }
  };
}

// ─── Hook lifecycle counters ─────────────────────────────────────────────────

function incrementSubscribers(): void {
  subscriberCount++;
  if (subscriberCount === 1) {
    connect();
  }
}

function decrementSubscribers(): void {
  subscriberCount = Math.max(0, subscriberCount - 1);
  if (subscriberCount === 0) {
    disconnect();
  }
}

// ─── Test helpers ─────────────────────────────────────────────────────────────

/** @internal — reset singleton for testing */
export function _resetForTesting(): void {
  disconnect();
  subscriberCount = 0;
  topicListeners.clear();
  reconnectCallbacks.clear();
}

/** @internal */
export function _getSubscriberCount(): number {
  return subscriberCount;
}

/** @internal */
export function _isConnected(): boolean {
  return ws !== null;
}

// ─── Reconnect callbacks ─────────────────────────────────────────────────────

/**
 * Register a callback that fires whenever the WebSocket reconnects.
 * Returns an unsubscribe function that removes the callback.
 *
 * Used by use-session-events to trigger gap-fill on reconnect so that
 * messages missed during the disconnection are fetched from the server.
 */
export function onReconnect(callback: () => void): () => void {
  const id = String(reconnectCallbackNextId++);
  reconnectCallbacks.set(id, callback);
  return () => {
    reconnectCallbacks.delete(id);
  };
}

// ─── Stable API object ────────────────────────────────────────────────────────

/**
 * Stable subscribe function — same reference across renders.
 * Components call this inside useEffect to subscribe to topics.
 */
const stableSubscribe = (topics: string[], callback: TopicCallback): Unsubscribe =>
  addTopicListeners(topics, callback);

export interface WeaveSocketAPI {
  /**
   * Subscribe to one or more topics.
   * Returns an unsubscribe function — call it in the useEffect cleanup.
   *
   * @example
   * useEffect(() => {
   *   return subscribe(["session:abc", "activity"], (topic, data) => { ... });
   * }, [subscribe]);
   */
  subscribe: (topics: string[], callback: TopicCallback) => Unsubscribe;
}

// ─── React hook ──────────────────────────────────────────────────────────────

/**
 * Access the Weave WebSocket singleton.
 *
 * On mount: increments the subscriber ref-count; connects if first subscriber.
 * On unmount: decrements; disconnects if last subscriber.
 *
 * Returns a stable `subscribe` function that manages topic subscriptions.
 */
export function useWeaveSocket(): WeaveSocketAPI {
  useEffect(() => {
    incrementSubscribers();
    return () => {
      decrementSubscribers();
    };
  }, []);

  const subscribe = useCallback(
    (topics: string[], callback: TopicCallback): Unsubscribe =>
      stableSubscribe(topics, callback),
    []
  );

  return { subscribe };
}
