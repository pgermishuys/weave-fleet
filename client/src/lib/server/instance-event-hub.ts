/**
 * Instance Event Hub — single shared event subscription per OpenCode instance.
 *
 * Replaces the 3 independent `client.event.subscribe()` calls per instance
 * (session-status-watcher, callback-monitor, per-session SSE) with a single
 * shared subscription that multiplexes events to all registered consumers.
 * Reduces SDK connection overhead from O(watchers + monitors + browser tabs)
 * to O(instances).
 *
 * Key design decisions:
 * - Snapshot dispatch: listeners Set is snapshotted before each dispatch loop
 *   so listeners can safely call their own unsubscribe during handling.
 * - Automatic reconnection: if the SDK stream ends or errors while listeners
 *   are still registered, the hub reconnects with exponential backoff.
 * - Distinct unsubscribes: each addListener() call returns a unique, idempotent
 *   unsubscribe function bound to that specific listener registration.
 *
 * Uses the globalThis singleton pattern for Turbopack compatibility.
 */

import { getInstance } from "./process-manager";
import { getClientForInstance } from "./opencode-client";
import { log } from "./logger";
import { withTimeout } from "./async-utils";

// ─── Types ────────────────────────────────────────────────────────────────────

// eslint-disable-next-line @typescript-eslint/no-explicit-any
export type InstanceEventListener = (event: { type: string; properties: Record<string, any> }) => void;

interface InstanceEntry {
  listeners: Set<InstanceEventListener>;
  abort: AbortController;
  directory: string;
  reconnecting: boolean;
}

// ─── globalThis-based singleton ───────────────────────────────────────────────

const _g = globalThis as unknown as {
  __weaveInstanceEventHub?: Map<string, InstanceEntry>;
};

function getHubState(): Map<string, InstanceEntry> {
  if (!_g.__weaveInstanceEventHub) {
    _g.__weaveInstanceEventHub = new Map();
  }
  return _g.__weaveInstanceEventHub;
}

// ─── Internal helpers ─────────────────────────────────────────────────────────

/**
 * Returns a Promise that resolves after `ms` milliseconds, or rejects early
 * if the abort signal fires. Used for cancellable reconnect backoff delays.
 */
function delay(ms: number, signal: AbortSignal): Promise<void> {
  return new Promise<void>((resolve, reject) => {
    if (signal.aborted) {
      reject(new Error("Aborted"));
      return;
    }
    const timer = setTimeout(() => {
      signal.removeEventListener("abort", onAbort);
      resolve();
    }, ms);
    function onAbort() {
      clearTimeout(timer);
      reject(new Error("Aborted"));
    }
    signal.addEventListener("abort", onAbort, { once: true });
  });
}

// ─── Core event-stream loop ───────────────────────────────────────────────────

/**
 * Main loop: subscribes to the SDK event stream for an instance and dispatches
 * events to all registered listeners. Automatically reconnects with exponential
 * backoff when the stream ends or errors, as long as listeners remain registered.
 */
async function processEventStream(instanceId: string, entry: InstanceEntry): Promise<void> {
  const hubState = getHubState();
  let backoffMs = 1_000;
  const MAX_BACKOFF_MS = 16_000;
  const subscribeTimeoutMs =
    parseInt(process.env.WEAVE_SUBSCRIBE_TIMEOUT_MS ?? "", 10) || 30_000;

  while (entry.listeners.size > 0) {
    try {
      const client = getClientForInstance(instanceId);
      const subscribeResult = await withTimeout(
        client.event.subscribe({ directory: entry.directory }),
        subscribeTimeoutMs,
        `event.subscribe for instance ${instanceId}`,
      );

      const eventStream =
        "stream" in subscribeResult
          ? (subscribeResult as { stream: AsyncIterable<unknown> }).stream
          : (subscribeResult as AsyncIterable<unknown>);

      // Successful connect — reset backoff
      backoffMs = 1_000;
      entry.reconnecting = false;

      for await (const rawEvent of eventStream) {
        if (entry.abort.signal.aborted) return;

        // eslint-disable-next-line @typescript-eslint/no-explicit-any
        const event = rawEvent as any;
        const type: string = event?.type ?? "unknown";
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
        const properties: Record<string, any> = event?.properties ?? event ?? {};

        // Snapshot listeners before dispatch — prevents undefined behavior when a
        // listener calls its unsubscribe (mutating the Set) during handling.
        const snapshot = [...entry.listeners];
        for (const listener of snapshot) {
          try {
            listener({ type, properties });
          } catch (err) {
            log.warn("instance-event-hub", "Listener threw during dispatch", { instanceId, err });
          }
        }
      }
      // Stream ended normally — fall through to reconnect logic below
    } catch (err) {
      if (entry.abort.signal.aborted) return;
      log.warn("instance-event-hub", "Stream error, will reconnect", { instanceId, err, backoffMs });
    }

    if (entry.abort.signal.aborted) return;

    // Check if instance is still alive before reconnecting
    const instance = getInstance(instanceId);
    if (!instance || instance.status === "dead") {
      log.warn("instance-event-hub", "Instance dead — stopping reconnect", { instanceId });
      break;
    }

    if (entry.listeners.size === 0) break; // all consumers gone

    entry.reconnecting = true;

    try {
      await delay(backoffMs, entry.abort.signal);
    } catch {
      // Backoff delay was cancelled (abort or all listeners removed)
      return;
    }

    backoffMs = Math.min(backoffMs * 2, MAX_BACKOFF_MS);

    if (entry.listeners.size === 0) break; // check again after delay
  }

  // Clean up entry — either all listeners left voluntarily, or instance died.
  // In the dead-instance case, listeners may still be registered but the stream
  // will never reconnect, so we must tear down to avoid a stale orphaned entry.
  entry.listeners.clear();
  entry.abort.abort();
  hubState.delete(instanceId);
}

// ─── Public API ───────────────────────────────────────────────────────────────

/**
 * Register a listener for events from the given instance. Starts a shared
 * SDK subscription for the instance on the first listener; subsequent listeners
 * reuse the same subscription.
 *
 * Returns a distinct, idempotent unsubscribe function bound to this specific
 * registration. Calling it removes only this listener.
 */
export function addListener(instanceId: string, listener: InstanceEventListener): () => void {
  const hubState = getHubState();

  let entry = hubState.get(instanceId);

  if (!entry) {
    // First listener for this instance — look up instance and start subscription
    const instance = getInstance(instanceId);
    if (!instance || instance.status === "dead") {
      log.warn("instance-event-hub", "Instance is dead — addListener is a no-op", { instanceId });
      // Return a no-op unsubscribe
      return () => { /* no-op */ };
    }

    entry = {
      listeners: new Set(),
      abort: new AbortController(),
      directory: instance.directory,
      reconnecting: false,
    };
    hubState.set(instanceId, entry);

    // Add listener BEFORE starting processEventStream so the while-loop
    // condition (entry.listeners.size > 0) is true on first check.
    entry.listeners.add(listener);

    // Start the event stream loop (fire-and-forget)
    void processEventStream(instanceId, entry);
  } else {
    entry.listeners.add(listener);
  }

  let removed = false;
  return () => {
    if (removed) return; // idempotent
    removed = true;

    const currentEntry = getHubState().get(instanceId);
    if (!currentEntry) return;

    currentEntry.listeners.delete(listener);

    if (currentEntry.listeners.size === 0) {
      // Last listener — abort the stream (cancels both active iteration and backoff delay)
      currentEntry.abort.abort();
      getHubState().delete(instanceId);
    }
  };
}

/**
 * Remove all listeners for an instance and tear down its subscription.
 * Called from process-manager on instance destruction as a safety net.
 */
export function removeAllListeners(instanceId: string): void {
  const hubState = getHubState();
  const entry = hubState.get(instanceId);
  if (!entry) return;

  entry.listeners.clear();
  entry.abort.abort();
  hubState.delete(instanceId);
}

/**
 * Reset all state — for tests only.
 */
export function _resetForTests(): void {
  const hubState = getHubState();
  for (const entry of hubState.values()) {
    entry.abort.abort();
  }
  hubState.clear();
}
