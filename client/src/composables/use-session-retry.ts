import { computed, onBeforeUnmount, reactive, readonly, shallowRef } from "vue";
import { api } from "@/api/client";
import { onReconnect, useWeaveSocket } from "@/composables/use-weave-socket";
import { toScheduledRetry, type ScheduledRetry } from "@/lib/turn-retry";

/** The Settings → Features switch. On unless the user turned it off. */
export const RETRY_AFTER_LIMITS_PREFERENCE_KEY = "RetryAfterLimits";

/** The `session.retry` event: a session's retry, or null when it has none waiting. Sent on its topic and `sessions`. */
export const RETRY_EVENT = "session.retry" as const;

/** Each session's retry as Fleet last said it, so switching back to a session shows it at once. */
const retries = reactive<Record<string, ScheduledRetry | null>>({});

/** The session's retry as last loaded or heard, without loading it: for what only needs to know whether one waits. */
export function scheduledRetryOf(sessionId: string): ScheduledRetry | null {
  return retries[sessionId] ?? null;
}

/**
 * A session's retry: when a model provider's limit stopped its turn, Fleet sends "Continue where you left off." when
 * the limit resets, or after a wait when the provider didn't say. The server keeps it and says so with
 * `session.retry`; this loads it when the session opens and again after a reconnect. Try now sends it at once, Don't
 * retry hands the session back to the user.
 */
export function useSessionRetry(sessionId: string) {
  const { subscribeV2 } = useWeaveSocket();
  const error = shallowRef<string | undefined>(undefined);
  const busy = shallowRef(false);
  const retry = computed<ScheduledRetry | null>(() => retries[sessionId] ?? null);
  let loadId = 0;

  async function load(): Promise<void> {
    const current = ++loadId;
    try {
      const { data, response } = await api.GET("/api/sessions/{id}/retry", { params: { path: { id: sessionId } } });
      if (current !== loadId || !response.ok) return;
      retries[sessionId] = response.status === 204 ? null : toScheduledRetry(data);
    } catch (loadError) {
      console.warn(`Failed to load the retry of session ${sessionId}:`, loadError);
    }
  }

  const unsubscribe = subscribeV2(
    `session:${sessionId}`,
    () => {
      // The retry loads from its own endpoint; snapshots don't carry it.
    },
    (event) => {
      if (event.type !== RETRY_EVENT) return;
      const payload = event.payload;
      if (payload?.sessionId !== sessionId) return;
      // Newer than any load in flight.
      loadId += 1;
      retries[sessionId] = toScheduledRetry(payload.retry);
    },
  );
  void load();
  const stopReconnect = onReconnect(() => void load());

  onBeforeUnmount(() => {
    unsubscribe();
    stopReconnect();
  });

  async function act(request: () => Promise<{ response: Response }>, failure: string): Promise<boolean> {
    error.value = undefined;
    busy.value = true;
    try {
      const { response } = await request();
      if (!response.ok && response.status !== 404) {
        error.value = `${failure} (HTTP ${response.status}).`;
        await load();
        return false;
      }
      // Fleet's event says so too; this covers a socket that's down.
      retries[sessionId] = null;
      return true;
    } catch (actError) {
      error.value = actError instanceof Error ? actError.message : `${failure}.`;
      return false;
    } finally {
      busy.value = false;
    }
  }

  /** Sends "Continue where you left off." now rather than when the limit resets. */
  function sendNow(): Promise<boolean> {
    return act(
      () => api.POST("/api/sessions/{id}/retry/send", { params: { path: { id: sessionId } } }),
      "Fleet couldn't try again now",
    );
  }

  /** Fleet doesn't try again; what the user queued goes out now. */
  function cancel(): Promise<boolean> {
    return act(
      () => api.DELETE("/api/sessions/{id}/retry", { params: { path: { id: sessionId } } }),
      "Fleet couldn't stop the retry",
    );
  }

  return { retry, busy: readonly(busy), error: readonly(error), sendNow, cancel };
}
