/**
 * Callback service — fires completion/error prompts to conductor sessions.
 *
 * All functions are best-effort: they wrap operations in try/catch so that
 * a callback failure never breaks the calling SSE stream.
 */

import {
  getPendingCallbacksForSession,
  claimPendingCallback,
  getSession,
  getSessionByHarnessId,
  type DbSession,
  type DbSessionCallback,
} from "./db-repository";
import { getInstance } from "./process-manager";
import { getClientForInstance } from "./opencode-client";
import { log } from "./logger";

// ─── Shared callback delivery ─────────────────────────────────────────────────

/**
 * Core loop that resolves the source session, iterates pending callbacks, and
 * delivers a message built by `buildMessage` to each conductor session.
 *
 * Returns silently on any error — never throws.
 */
async function deliverCallbacks(
  sourceSessionId: string,
  buildMessage: (dbSession: DbSession, callback: DbSessionCallback) => Promise<string> | string
): Promise<void> {
  try {
    // 1. Resolve the OpenCode session ID to the Fleet DB session
    const dbSession = getSessionByHarnessId(sourceSessionId);
    if (!dbSession) return;

    // 2. Get pending callbacks — no-op when feature is not used
    const callbacks = getPendingCallbacksForSession(dbSession.id);
    if (callbacks.length === 0) return;

    // 3. Fire each callback
    for (const callback of callbacks) {
      try {
        // Atomically claim this callback — prevents duplicate delivery
        if (!claimPendingCallback(callback.id)) continue;

        // a. Get target instance (conductor)
        const targetInstance = getInstance(callback.target_instance_id);
        if (!targetInstance || targetInstance.status === "dead") continue;

        // b. Get target session DB record (for opencode_session_id)
        const targetDbSession = getSession(callback.target_session_id);
        if (!targetDbSession) continue;

        // c. Build the message (may be async for diff lookups)
        const callbackMessage = await buildMessage(dbSession, callback);

        // d. Send prompt to conductor
        await targetInstance.client.session.promptAsync({
          sessionID: targetDbSession.opencode_session_id,
          parts: [{ type: "text", text: callbackMessage }],
        });
      } catch (err) {
        // Individual callback failure — continue with remaining callbacks
        log.error("callback-service", "Failed to deliver callback", { callbackId: callback.id, err });
      }
    }
  } catch (err) {
    // Top-level guard — never throws
    log.error("callback-service", "deliverCallbacks failed", { err });
  }
}

// ─── Public API ───────────────────────────────────────────────────────────────

export async function fireSessionCallbacks(
  sourceSessionId: string,
  instanceId: string
): Promise<void> {
  return deliverCallbacks(sourceSessionId, async (dbSession) => {
    // Gather diff summary from the completed child
    let diffSummary = "";
    try {
      const childClient = getClientForInstance(instanceId);
      const result = await childClient.session.diff({ sessionID: sourceSessionId });
      const diffs = result.data ?? [];
      if (diffs.length > 0) {
        const lines = diffs.map((d) => {
          const changeType = !d.before ? "  added:" : !d.after ? "  deleted:" : "  modified:";
          return `${changeType} ${d.file}`;
        });
        diffSummary = `Files changed: ${diffs.length}\n${lines.join("\n")}`;
      } else {
        diffSummary = "Files changed: 0";
      }
    } catch {
      diffSummary = "(diff unavailable)";
    }

    return [
      "[Fleet Callback] Child session completed.",
      `Session ID: ${dbSession.id}`,
      `Title: ${dbSession.title}`,
      diffSummary,
      "Status: idle (completed successfully)",
    ].join("\n");
  });
}

export async function fireSessionErrorCallbacks(
  sourceSessionId: string,
  _instanceId: string
): Promise<void> {
  return deliverCallbacks(sourceSessionId, (dbSession) => {
    return [
      "[Fleet Callback] Child session encountered an error.",
      `Session ID: ${dbSession.id}`,
      `Title: ${dbSession.title}`,
      "Status: error",
    ].join("\n");
  });
}
