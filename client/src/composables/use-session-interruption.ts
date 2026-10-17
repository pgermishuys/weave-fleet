import { computed, onBeforeUnmount, reactive, readonly, shallowRef } from "vue";
import { onReconnect, useWeaveSocket } from "@/composables/use-weave-socket";
import { toSessionInterruption, type SessionInterruption } from "@/lib/session-interruption";
import { useMachineTarget } from "@/lib/machine-target";

/** The `session.interrupted` event: a session's interruption, or null once it has none. Sent on its topic and `sessions`. */
export const INTERRUPTED_EVENT = "session.interrupted" as const;

/** Each session's interruption as Fleet last said it, so switching back to a session shows it at once. */
const interruptions = reactive<Record<string, SessionInterruption | null>>({});

/** The session's interruption as last loaded or heard, without loading it: for the session list's rows. */
export function interruptionOf(sessionId: string): SessionInterruption | null {
  return interruptions[sessionId] ?? null;
}

/**
 * A session's interruption: a Fleet restart cut its turn off. The server keeps it and says so with
 * `session.interrupted`; this loads it when the session opens and again after a reconnect. Continue sends "Continue
 * where you left off." as the user's message, Dismiss forgets it.
 */
export function useSessionInterruption(sessionId: string) {
  const machine = useMachineTarget();
  const { subscribeV2 } = useWeaveSocket(machine);
  const error = shallowRef<string | undefined>(undefined);
  const busy = shallowRef(false);
  const interruption = computed<SessionInterruption | null>(() => interruptions[sessionId] ?? null);
  let loadId = 0;

  async function load(): Promise<void> {
    const current = ++loadId;
    try {
      const { data, response } = await machine.api.GET("/api/sessions/{id}/interruption", { params: { path: { id: sessionId } } });
      if (current !== loadId || !response.ok) return;
      interruptions[sessionId] = response.status === 204 ? null : toSessionInterruption(data);
    } catch (loadError) {
      console.warn(`Failed to load the interruption of session ${sessionId}:`, loadError);
    }
  }

  const unsubscribe = subscribeV2(
    `session:${sessionId}`,
    () => {
      // The interruption loads from its own endpoint; snapshots don't carry it.
    },
    (event) => {
      if (event.type !== INTERRUPTED_EVENT) return;
      const payload = event.payload;
      if (payload?.sessionId !== sessionId) return;
      // Newer than any load in flight.
      loadId += 1;
      interruptions[sessionId] = toSessionInterruption(payload.interruption);
    },
  );
  void load();
  const stopReconnect = onReconnect(machine, () => void load());

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
      interruptions[sessionId] = null;
      return true;
    } catch (actError) {
      error.value = actError instanceof Error ? actError.message : `${failure}.`;
      return false;
    } finally {
      busy.value = false;
    }
  }

  /** Sends "Continue where you left off." (or asks the lost question again). */
  function continueSession(): Promise<boolean> {
    return act(
      () => machine.api.POST("/api/sessions/{id}/interruption/continue", { params: { path: { id: sessionId } } }),
      "Fleet couldn't continue the session",
    );
  }

  /** Forgets the interruption without sending anything. */
  function dismiss(): Promise<boolean> {
    return act(
      () => machine.api.DELETE("/api/sessions/{id}/interruption", { params: { path: { id: sessionId } } }),
      "Fleet couldn't dismiss this",
    );
  }

  return { interruption, busy: readonly(busy), error: readonly(error), continueSession, dismiss };
}
