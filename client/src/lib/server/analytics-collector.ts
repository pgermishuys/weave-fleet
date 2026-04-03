/**
 * Analytics Collector — in-memory token/cost accumulation with batched DB flush.
 *
 * Decouples high-frequency analytical work (token/cost tracking from
 * `message.part.updated` step-finish events) from the synchronous Instance Event
 * Hub dispatch loop. Instead of writing to SQLite and broadcasting via EventEmitter
 * on every event, the collector:
 *
 * 1. Accumulates token/cost deltas in a Map (O(1), no I/O)
 * 2. Flushes accumulated data to the DB on a timer (default: 2s)
 * 3. Broadcasts one `emitTokenUpdate` per session per flush (not per event)
 * 4. Wraps all DB writes in a single SQLite transaction (one WAL lock acquisition)
 *
 * This reduces WAL lock acquisitions from ~100+/2s (at 13 sessions) to 1/2s.
 *
 * Uses the globalThis singleton pattern for Turbopack compatibility.
 */

import { getSessionByHarnessId, incrementSessionTokens } from "./db-repository";
import { emitTokenUpdate } from "./activity-emitter";
import { getDb } from "./database";
import { log } from "./logger";

// ─── Types ────────────────────────────────────────────────────────────────────

interface PendingTokenData {
  tokensDelta: number;
  costDelta: number;
}

// ─── globalThis-based singletons ──────────────────────────────────────────────

const _g = globalThis as unknown as {
  __weaveAnalyticsPending?: Map<string, PendingTokenData>;
  __weaveAnalyticsFlushTimer?: ReturnType<typeof setInterval> | null;
};

function getPendingMap(): Map<string, PendingTokenData> {
  if (!_g.__weaveAnalyticsPending) {
    _g.__weaveAnalyticsPending = new Map();
  }
  return _g.__weaveAnalyticsPending;
}

// Re-entrant flush guard — module-level (not globalThis) because it's only
// meaningful within a single Node.js process tick and doesn't need to survive
// Turbopack re-evaluations.
let _flushing = false;

// ─── Core accumulation ────────────────────────────────────────────────────────

/**
 * Record a token/cost delta for a session. O(1) Map write — no DB, no EventEmitter.
 * Called from the Instance Event Hub dispatch loop for every step-finish event.
 */
export function recordTokens(
  harnessSessionId: string,
  tokensDelta: number,
  costDelta: number,
): void {
  const pending = getPendingMap();
  const existing = pending.get(harnessSessionId);
  if (existing) {
    existing.tokensDelta += tokensDelta;
    existing.costDelta += costDelta;
  } else {
    pending.set(harnessSessionId, { tokensDelta, costDelta });
  }
}

// ─── Flush logic ──────────────────────────────────────────────────────────────

/**
 * Internal flush — drains the pending map into the DB and emits token updates.
 * All DB writes are wrapped in a single SQLite transaction for atomicity and
 * single WAL lock acquisition.
 */
function flush(): void {
  if (_flushing) return;

  const pending = getPendingMap();
  if (pending.size === 0) return;

  _flushing = true;

  try {
    // Snapshot pending entries and clear the map immediately so new events
    // that arrive during flush go into the next window, not this one.
    const snapshot = new Map(pending);
    pending.clear();

    // Collect emits to perform after the transaction commits
    const emits: Array<{ sessionId: string; totalTokens: number; totalCost: number }> = [];

    const db = getDb();
    const runTransaction = db.transaction(() => {
      for (const [harnessSessionId, entry] of snapshot) {
        try {
          const dbSession = getSessionByHarnessId(harnessSessionId);
          if (!dbSession) {
            log.warn("analytics-collector", "Session not found during flush — discarding pending tokens", {
              harnessSessionId,
              tokensDelta: entry.tokensDelta,
              costDelta: entry.costDelta,
            });
            continue;
          }

          const totals = incrementSessionTokens(dbSession.id, entry.tokensDelta, entry.costDelta);
          if (totals) {
            emits.push({
              sessionId: harnessSessionId,
              totalTokens: totals.totalTokens,
              totalCost: totals.totalCost,
            });
          }
        } catch (err) {
          log.warn("analytics-collector", "Failed to flush token data for session", {
            harnessSessionId,
            err,
          });
        }
      }
    });

    runTransaction();

    // Emit token updates after the transaction — outside the write lock
    for (const payload of emits) {
      try {
        emitTokenUpdate(payload);
      } catch (err) {
        log.warn("analytics-collector", "Failed to emit token update", { sessionId: payload.sessionId, err });
      }
    }
  } catch (err) {
    log.error("analytics-collector", "Flush failed — clearing pending map to prevent unbounded growth", { err });
    // Data loss is acceptable — token counts are non-critical counters.
    // Clearing prevents memory growth if DB is persistently unavailable.
    getPendingMap().clear();
  } finally {
    _flushing = false;
  }
}

// ─── Public lifecycle API ─────────────────────────────────────────────────────

/**
 * Trigger an immediate flush of all pending token data.
 * Called by tests and by graceful shutdown (`destroyAll`).
 */
export function flushNow(): void {
  flush();
}

/**
 * Returns the number of sessions with pending (unflushed) token data.
 * Useful for tests and diagnostics.
 */
export function getPendingCount(): number {
  return getPendingMap().size;
}

/**
 * Start the periodic flush timer. Idempotent — no-op if already running.
 * Called once after Fleet startup recovery completes.
 *
 * @param intervalMs Flush interval in milliseconds (default: WEAVE_ANALYTICS_FLUSH_INTERVAL_MS or 2000)
 */
export function startCollector(intervalMs?: number): void {
  if (_g.__weaveAnalyticsFlushTimer) return; // already running

  const resolvedInterval =
    intervalMs ??
    (parseInt(process.env.WEAVE_ANALYTICS_FLUSH_INTERVAL_MS ?? "", 10) || 2_000);

  const timer = setInterval(() => {
    flush();
  }, resolvedInterval);

  // Ensure the timer doesn't prevent Node.js from exiting
  if (timer.unref) {
    timer.unref();
  }

  _g.__weaveAnalyticsFlushTimer = timer;
}

/**
 * Stop the periodic flush timer. Idempotent — no-op if not running.
 * Does NOT flush — call `flushNow()` first if you need to persist pending data.
 */
export function stopCollector(): void {
  if (_g.__weaveAnalyticsFlushTimer) {
    clearInterval(_g.__weaveAnalyticsFlushTimer);
    _g.__weaveAnalyticsFlushTimer = null;
  }
}

/**
 * Reset all state — for tests only.
 * Stops the flush timer and clears the pending map.
 */
export function _resetForTests(): void {
  stopCollector();
  getPendingMap().clear();
  _flushing = false;
}
