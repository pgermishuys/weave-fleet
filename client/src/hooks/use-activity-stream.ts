/**
 * Shared activity stream with ref-counted subscriptions.
 *
 * Drop-in replacement for `useGlobalSSE` that uses the Weave WebSocket
 * singleton instead of a dedicated SSE EventSource. Subscribes to the
 * "activity" topic which receives the same `activity_status` and
 * `token_update` events previously delivered over `/api/activity-stream`.
 *
 * API is intentionally identical to `useGlobalSSE` so that callers can
 * swap the import without changing any other code.
 *
 * Usage:
 *
 *   const stream = useActivityStream();
 *
 *   useEffect(() => {
 *     stream.on("activity_status", (payload) => { ... });
 *     return () => stream.off("activity_status", handler);
 *   }, [stream]);
 */

import { useEffect } from "react";
import { useWeaveSocket, type TopicCallback } from "@/hooks/use-weave-socket";

// ─── Types ───────────────────────────────────────────────────────────────────

type ActivityCallback = (payload: unknown) => void;

export interface ActivityStreamSubscription {
  /** Register a callback for a specific event type (e.g. "activity_status"). */
  on(eventType: string, callback: ActivityCallback): void;
  /** Remove a previously registered callback. */
  off(eventType: string, callback: ActivityCallback): void;
}

// ─── Module-level listener registry ─────────────────────────────────────────
//
// We keep event-type listeners at module level so they survive React re-renders
// and are not re-registered on every render (same pattern as use-weave-socket.ts).
//
// The WebSocket subscription is managed per hook instance (mount/unmount) but
// all hook instances share the same event-type listener registry below.

/** eventType → set of callbacks */
const eventListeners = new Map<string, Set<ActivityCallback>>();

function dispatchEvent(eventType: string, payload: unknown): void {
  const callbacks = eventListeners.get(eventType);
  if (!callbacks) return;
  for (const cb of callbacks) {
    cb(payload);
  }
}

function addEventCallback(eventType: string, callback: ActivityCallback): void {
  let set = eventListeners.get(eventType);
  if (!set) {
    set = new Set();
    eventListeners.set(eventType, set);
  }
  set.add(callback);
}

function removeEventCallback(eventType: string, callback: ActivityCallback): void {
  const set = eventListeners.get(eventType);
  if (!set) return;
  set.delete(callback);
  if (set.size === 0) {
    eventListeners.delete(eventType);
  }
}

// ─── Stable API object ────────────────────────────────────────────────────────

const stableSubscription: ActivityStreamSubscription = {
  on: addEventCallback,
  off: removeEventCallback,
};

// ─── Test helpers ─────────────────────────────────────────────────────────────

/** @internal — reset listener registry for testing */
export function _resetListenersForTesting(): void {
  eventListeners.clear();
}

// ─── React hook ──────────────────────────────────────────────────────────────

const ACTIVITY_TOPIC = "activity";

/**
 * Subscribe to the Weave activity stream.
 *
 * On mount: subscribes to the "activity" WebSocket topic (ref-counted via
 * useWeaveSocket).
 * On unmount: unsubscribes from the topic.
 *
 * Returns a stable `on`/`off` API for registering typed event callbacks.
 * The returned object is referentially stable — safe to use as a dependency.
 */
export function useActivityStream(): ActivityStreamSubscription {
  const { subscribe } = useWeaveSocket();

  useEffect(() => {
    // Route all "activity" topic messages to the event-type dispatcher
    const topicCallback: TopicCallback = (_topic: string, data: unknown) => {
      const msg = data as { type?: string } | null;
      if (!msg?.type) return;
      dispatchEvent(msg.type, data);
    };

    const unsub = subscribe([ACTIVITY_TOPIC], topicCallback);
    return unsub;
  }, [subscribe]);

  return stableSubscription;
}
