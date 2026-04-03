/**
 * Callback Monitor — server-side event listener that fires completion
 * callbacks without depending on a browser SSE connection.
 *
 * Three layers of redundancy:
 * 1. SSE handler (instant, browser-dependent) — existing, unchanged
 * 2. Event subscription (instant, server-side) — this module
 * 3. Polling fallback (10s delay, catches everything) — this module
 *
 * Duplicate delivery is prevented by the atomic `claimPendingCallback()` in
 * db-repository, which ensures only one caller succeeds per callback row.
 *
 * Architecture:
 * - Registers one listener per running OpenCode instance on the shared
 *   Instance Event Hub (which owns the actual SDK subscription and reconnection).
 * - Uses ref-counting: first `startMonitoring()` call for a given instance
 *   registers a hub listener; the listener is removed when the last monitored
 *   session on that instance is stopped.
 *
 * Uses the globalThis singleton pattern (matching process-manager.ts) for
 * Turbopack compatibility.
 */

import {
  getAllPendingCallbacks,
  claimPendingCallback,
  getSession,
  updateSessionStatus,
} from "./db-repository";
import { getInstance, _recoveryComplete } from "./process-manager";
import { addListener } from "./instance-event-hub";
import {
  fireSessionCallbacks,
  fireSessionErrorCallbacks,
} from "./callback-service";
import { withTimeout, getSDKCallTimeoutMs } from "./async-utils";
import { log } from "./logger";

// ─── Constants ────────────────────────────────────────────────────────────────

const CALLBACK_POLL_INTERVAL_MS = 10_000;
/** Stop polling after this many consecutive polls find no pending callbacks. */
const MAX_EMPTY_POLLS = 3;

// ─── Types ────────────────────────────────────────────────────────────────────

interface MonitoredSession {
  dbSessionId: string;
  harnessSessionId: string;
  instanceId: string;
}

interface InstanceMonitorState {
  sessionStates: Map<string, "idle" | "busy">; // keyed by harness session ID
  monitoredDbSessionIds: Set<string>;           // Fleet DB IDs being monitored on this instance
  unsubscribe: () => void;                      // hub unsubscribe for this instance
}

// ─── globalThis-based singletons ──────────────────────────────────────────────

const _g = globalThis as unknown as {
  __weaveCallbackMonitor?: {
    monitoredSessions: Map<string, MonitoredSession>;
    instanceMonitorStates: Map<string, InstanceMonitorState>;
  };
  __weaveCallbackPollInterval?: ReturnType<typeof setInterval> | null;
  __weaveCallbackMonitorInit?: boolean;
  __weaveCallbackConsecutiveEmptyPolls?: number;
};

function getMonitorState() {
  if (!_g.__weaveCallbackMonitor) {
    _g.__weaveCallbackMonitor = {
      monitoredSessions: new Map(),
      instanceMonitorStates: new Map(),
    };
  }
  return _g.__weaveCallbackMonitor;
}

// ─── Event Handler Builder ────────────────────────────────────────────────────

/**
 * Build the hub event handler for a given instance. The handler detects
 * busy→idle transitions for monitored sessions and fires callbacks.
 *
 * NOTE: The handler calls stopMonitoringSession() during dispatch (break after
 * first match ensures safe iteration of monitoredSessions). The hub's snapshot
 * dispatch makes this safe at the hub level.
 */
function buildHandler(instanceId: string) {
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  return function handleEvent({ type, properties }: { type: string; properties: Record<string, any> }): void {
    const { monitoredSessions, instanceMonitorStates } = getMonitorState();

    const state = instanceMonitorStates.get(instanceId);
    if (!state) return; // state was removed

    if (type === "session.status") {
      const statusType: string = properties?.status?.type ?? "";
      const eventSessionId: string =
        properties?.sessionID ?? properties?.info?.id ?? "";

      if (!eventSessionId) return;

      if (statusType === "busy") {
        state.sessionStates.set(eventSessionId, "busy");
      } else if (statusType === "idle") {
        const prevState = state.sessionStates.get(eventSessionId);
        state.sessionStates.set(eventSessionId, "idle");

        if (prevState === "busy") {
          // Find the monitored session by opencode session ID
          for (const [dbSessionId, monitored] of monitoredSessions) {
            if (
              monitored.harnessSessionId === eventSessionId &&
              monitored.instanceId === instanceId
            ) {
              try {
                updateSessionStatus(dbSessionId, "idle");
                void fireSessionCallbacks(eventSessionId, instanceId);
              } catch (err) {
                log.error("callback-monitor", "Failed to fire callback for session", { dbSessionId, err });
              }
              stopMonitoringSession(dbSessionId);
              break;
            }
          }
        }
      }
    } else if (type === "session.idle") {
      const eventSessionId: string =
        properties?.sessionID ?? properties?.info?.id ?? "";
      if (!eventSessionId) return;

      const prevState = state.sessionStates.get(eventSessionId);
      state.sessionStates.set(eventSessionId, "idle");

      if (prevState === "busy") {
        for (const [dbSessionId, monitored] of monitoredSessions) {
          if (
            monitored.harnessSessionId === eventSessionId &&
            monitored.instanceId === instanceId
          ) {
            try {
              updateSessionStatus(dbSessionId, "idle");
              void fireSessionCallbacks(eventSessionId, instanceId);
            } catch (err) {
              log.error("callback-monitor", "Failed to fire callback for session", { dbSessionId, err });
            }
            stopMonitoringSession(dbSessionId);
            break;
          }
        }
      }
    } else if (type === "error") {
      const eventSessionId: string =
        properties?.sessionID ?? properties?.info?.id ?? "";
      if (!eventSessionId) return;

      for (const [dbSessionId, monitored] of monitoredSessions) {
        if (
          monitored.harnessSessionId === eventSessionId &&
          monitored.instanceId === instanceId
        ) {
          try {
            void fireSessionErrorCallbacks(eventSessionId, instanceId);
          } catch (err) {
            log.error("callback-monitor", "Failed to fire error callback for session", { dbSessionId, err });
          }
          stopMonitoringSession(dbSessionId);
          break;
        }
      }
    }
  };
}

// ─── Subscription Management ──────────────────────────────────────────────────

/**
 * Internal: remove a session from monitoring state and clean up instance
 * monitor state if no more sessions are being monitored on it.
 */
function stopMonitoringSession(dbSessionId: string): void {
  const { monitoredSessions, instanceMonitorStates } = getMonitorState();

  const monitored = monitoredSessions.get(dbSessionId);
  if (!monitored) return;

  monitoredSessions.delete(dbSessionId);

  // Remove from instance monitor state tracking
  const state = instanceMonitorStates.get(monitored.instanceId);
  if (state) {
    state.monitoredDbSessionIds.delete(dbSessionId);
    state.sessionStates.delete(monitored.harnessSessionId);

    // If no more sessions on this instance, tear down the hub listener
    if (state.monitoredDbSessionIds.size === 0) {
      state.unsubscribe();
      instanceMonitorStates.delete(monitored.instanceId);
    }
  }
}

// ─── Public API ───────────────────────────────────────────────────────────────

/**
 * Start monitoring a child session for busy→idle transitions.
 * Registers a hub listener for the instance on the first monitored session;
 * subsequent sessions on the same instance share the same listener.
 * Performs an initial status poll to catch already-idle sessions.
 */
export function startMonitoring(
  dbSessionId: string,
  harnessSessionId: string,
  instanceId: string
): void {
  const { monitoredSessions, instanceMonitorStates } = getMonitorState();

  // Idempotent — skip if already monitoring
  if (monitoredSessions.has(dbSessionId)) return;

  monitoredSessions.set(dbSessionId, {
    dbSessionId,
    harnessSessionId,
    instanceId,
  });

  // Restart polling loop if it was paused due to inactivity
  if (!_g.__weaveCallbackPollInterval) {
    _g.__weaveCallbackConsecutiveEmptyPolls = 0;
    startCallbackPollingLoop();
  }

  // Add to existing instance monitor state or create new one
  let state = instanceMonitorStates.get(instanceId);
  if (state) {
    state.monitoredDbSessionIds.add(dbSessionId);
  } else {
    // First session on this instance — register a hub listener
    const instance = getInstance(instanceId);
    if (!instance || instance.status === "dead") {
      log.warn("callback-monitor", "Instance is dead — cannot monitor session", { instanceId, dbSessionId });
      monitoredSessions.delete(dbSessionId);
      return;
    }

    const handler = buildHandler(instanceId);
    const unsubscribe = addListener(instanceId, handler);

    state = {
      sessionStates: new Map(),
      monitoredDbSessionIds: new Set([dbSessionId]),
      unsubscribe,
    };
    instanceMonitorStates.set(instanceId, state);
  }

  // Initial status poll — catch already-idle sessions
  void (async () => {
    try {
      const instance = getInstance(instanceId);
      if (!instance || instance.status === "dead") return;

      const result = await withTimeout(
        instance.client.session.status({ directory: instance.directory }),
        getSDKCallTimeoutMs(),
        `session.status initial poll for instance ${instanceId}`,
      );
      const statusMap = (result.data ?? {}) as Record<string, { type: string }>;
      const liveStatus = statusMap[harnessSessionId];

      if (liveStatus?.type === "idle") {
        // Already idle — fire callback immediately
        const dbSession = getSession(dbSessionId);
        if (dbSession) {
          updateSessionStatus(dbSessionId, "idle");
          void fireSessionCallbacks(harnessSessionId, instanceId);
        }
        stopMonitoringSession(dbSessionId);
      } else if (liveStatus?.type === "busy") {
        // Mark as busy in state so we can detect the transition
        const currentState = instanceMonitorStates.get(instanceId);
        if (currentState) {
          currentState.sessionStates.set(harnessSessionId, "busy");
        }
      }
    } catch (err) {
      log.warn("callback-monitor", "Initial status poll failed for session", { dbSessionId, err });
      // Non-fatal — event subscription or polling will catch it
    }
  })();
}

/**
 * Stop monitoring a session. Called when a session is deleted or terminated.
 */
export function stopMonitoring(dbSessionId: string): void {
  stopMonitoringSession(dbSessionId);
}

// ─── Polling Safety Net ───────────────────────────────────────────────────────

/**
 * Start a periodic polling loop that checks all pending callbacks and fires
 * any whose sessions have gone idle. This catches cases where the event
 * subscription misses a transition (e.g., subscription started after
 * completion, instance reconnected, etc.).
 *
 * Idempotent — only one loop runs at a time.
 */
export function startCallbackPollingLoop(): void {
  if (_g.__weaveCallbackPollInterval) return;

  _g.__weaveCallbackPollInterval = setInterval(async () => {
    try {
      const pending = getAllPendingCallbacks();
      if (pending.length === 0) {
        _g.__weaveCallbackConsecutiveEmptyPolls = (_g.__weaveCallbackConsecutiveEmptyPolls ?? 0) + 1;
        if (_g.__weaveCallbackConsecutiveEmptyPolls >= MAX_EMPTY_POLLS) {
          // No pending callbacks for several consecutive polls — pause the loop
          if (_g.__weaveCallbackPollInterval) {
            clearInterval(_g.__weaveCallbackPollInterval);
            _g.__weaveCallbackPollInterval = null;
          }
        }
        return;
      }
      _g.__weaveCallbackConsecutiveEmptyPolls = 0;

      // Group by source session's instance to batch status checks
      const byInstance = new Map<string, typeof pending>();
      for (const cb of pending) {
        const sourceSession = getSession(cb.source_session_id);
        if (!sourceSession) {
          // Source session deleted — claim and skip
          claimPendingCallback(cb.id);
          continue;
        }
        const list = byInstance.get(sourceSession.instance_id) ?? [];
        list.push(cb);
        byInstance.set(sourceSession.instance_id, list);
      }

      for (const [instanceId, callbacks] of byInstance) {
        const instance = getInstance(instanceId);
        if (!instance || instance.status === "dead") {
          // Instance dead — fire error callbacks for each
          for (const cb of callbacks) {
            const sourceSession = getSession(cb.source_session_id);
            if (sourceSession) {
              void fireSessionErrorCallbacks(
                sourceSession.opencode_session_id,
                instanceId
              );
            }
          }
          continue;
        }

        // Poll session statuses
        try {
          const result = await withTimeout(
            instance.client.session.status({ directory: instance.directory }),
            getSDKCallTimeoutMs(),
            `session.status poll for instance ${instanceId}`,
          );
          const statusMap = (result.data ?? {}) as Record<
            string,
            { type: string }
          >;

          for (const cb of callbacks) {
            const sourceSession = getSession(cb.source_session_id);
            if (!sourceSession) continue;

            const liveStatus = statusMap[sourceSession.opencode_session_id];
            if (liveStatus?.type === "idle") {
              // Session is idle — fire the callback
              void fireSessionCallbacks(
                sourceSession.opencode_session_id,
                instanceId
              );
              // Also update DB status
              if (sourceSession.status !== "idle") {
                updateSessionStatus(sourceSession.id, "idle");
              }
              // Stop monitoring if we were monitoring
              stopMonitoringSession(sourceSession.id);
            }
          }
        } catch (err) {
          log.warn("callback-monitor", "Polling status for instance failed", { instanceId, err });
        }
      }
    } catch (err) {
      log.error("callback-monitor", "Polling loop error", { err });
    }
  }, CALLBACK_POLL_INTERVAL_MS);
}

/**
 * Reset all internal state — for tests only.
 */
export function _resetForTests(): void {
  const state = getMonitorState();

  // Unsubscribe all hub listeners
  for (const monitorState of state.instanceMonitorStates.values()) {
    monitorState.unsubscribe();
  }

  state.monitoredSessions.clear();
  state.instanceMonitorStates.clear();

  if (_g.__weaveCallbackPollInterval) {
    clearInterval(_g.__weaveCallbackPollInterval);
    _g.__weaveCallbackPollInterval = null;
  }
  _g.__weaveCallbackConsecutiveEmptyPolls = 0;
  _g.__weaveCallbackMonitorInit = false;
}

// ─── Self-initializing startup ────────────────────────────────────────────────
// Start the polling loop after instance recovery completes.
// Guarded so it only runs once across Turbopack re-evaluations.

if (!_g.__weaveCallbackMonitorInit) {
  _g.__weaveCallbackMonitorInit = true;
  _recoveryComplete
    .then(() => {
      startCallbackPollingLoop();
    })
    .catch(() => {
      /* non-fatal */
    });
}
