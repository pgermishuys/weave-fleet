import { computed, onBeforeUnmount, reactive, readonly, shallowRef } from "vue";
import { api, type ModelReference } from "@/api/client";
import { onReconnect, useWeaveSocket } from "@/composables/use-weave-socket";

/** How a queued item goes out: a message, a slash command, or a shell command (`!git status`). */
export type QueuedMessageKind = "prompt" | "command" | "shell";

export interface QueuedMessage {
  id: string;
  text: string;
  kind: QueuedMessageKind;
}

/** What goes with a queued item besides its text: how it goes out, and what the composer had picked. */
export interface QueueOptions {
  kind: QueuedMessageKind;
  /** For a slash command: its name and arguments. */
  command?: string;
  arguments?: string;
  agent?: string;
  model?: ModelReference;
  effort?: string;
}

/** The `session.queue` event: a session's whole queue, sent whenever it changes. */
const QUEUE_EVENT = "session.queue" as const;

/** Each session's queue as Fleet last said it, so switching back to a session shows it at once. */
const queues = reactive<Record<string, QueuedMessage[]>>({});

function toQueuedMessage(value: unknown): QueuedMessage | null {
  if (!value || typeof value !== "object") return null;
  const { id, text, kind } = value as Record<string, unknown>;
  if (typeof id !== "string" || typeof text !== "string") return null;
  return { id, text, kind: kind === "command" || kind === "shell" ? kind : "prompt" };
}

function toQueue(values: unknown): QueuedMessage[] {
  return Array.isArray(values) ? values.map(toQueuedMessage).filter((item): item is QueuedMessage => item !== null) : [];
}

/** The reason in an error body: Fleet's `{ error }`, or a problem's `detail` or `title`. */
function errorMessage(body: unknown, fallback: string): string {
  if (body && typeof body === "object") {
    for (const key of ["error", "detail", "title"]) {
      const value = (body as Record<string, unknown>)[key];
      if (typeof value === "string" && value.trim().length > 0) return value;
    }
  }
  return fallback;
}

/**
 * A session's queue: the messages the user sent while its agent worked. The queue is Fleet's, not the browser's:
 * the server keeps it, sends the first when the turn ends and the next when that turn ends, and says so with
 * `session.queue` on the session's topic. So leaving the session, reloading or closing the tab loses nothing, and
 * every open client shows the same queue. Loaded when the session opens and again after a reconnect.
 */
export function useSessionQueue(sessionId: string) {
  const { subscribeV2 } = useWeaveSocket();
  const error = shallowRef<string | undefined>(undefined);
  const queue = computed<readonly QueuedMessage[]>(() => queues[sessionId] ?? []);
  let loadId = 0;

  async function load(): Promise<void> {
    const current = ++loadId;
    try {
      const { data, response } = await api.GET("/api/sessions/{id}/queue", { params: { path: { id: sessionId } } });
      if (current !== loadId || !response.ok) return;
      queues[sessionId] = toQueue(data);
    } catch (loadError) {
      console.warn(`Failed to load the queue of session ${sessionId}:`, loadError);
    }
  }

  const unsubscribe = subscribeV2(
    `session:${sessionId}`,
    () => {
      // The queue loads from its own endpoint; snapshots don't carry it.
    },
    (event) => {
      if (event.type !== QUEUE_EVENT) return;
      const payload = event.payload;
      if (payload?.sessionId !== sessionId) return;
      // Newer than any load in flight.
      loadId += 1;
      queues[sessionId] = toQueue(payload.items);
    },
  );
  void load();
  const stopReconnect = onReconnect(() => void load());

  onBeforeUnmount(() => {
    unsubscribe();
    stopReconnect();
  });

  /** Queues `text`; Fleet sends it when the turn ends. False, with `error` set, when Fleet refused it. */
  async function enqueue(text: string, options: QueueOptions): Promise<boolean> {
    error.value = undefined;
    try {
      const { data, error: body, response } = await api.POST("/api/sessions/{id}/queue", {
        params: { path: { id: sessionId } },
        body: {
          text,
          kind: options.kind,
          command: options.command ?? null,
          arguments: options.arguments ?? null,
          agent: options.agent ?? null,
          model: options.model ?? null,
          effort: options.effort ?? null,
        },
      });
      if (!response.ok) {
        error.value = errorMessage(body, `The message couldn't be queued (HTTP ${response.status}).`);
        return false;
      }
      // The event usually says so first; this covers a socket that's down.
      const item = toQueuedMessage(data);
      const current = queues[sessionId] ?? [];
      if (item && !current.some((queued) => queued.id === item.id)) queues[sessionId] = [...current, item];
      return true;
    } catch (enqueueError) {
      error.value = enqueueError instanceof Error ? enqueueError.message : "The message couldn't be queued.";
      return false;
    }
  }

  /** Takes a queued item back. It goes at once; if Fleet refuses, the queue is loaded again. */
  async function remove(itemId: string): Promise<void> {
    queues[sessionId] = (queues[sessionId] ?? []).filter((item) => item.id !== itemId);
    try {
      const { response } = await api.DELETE("/api/sessions/{id}/queue/{itemId}", { params: { path: { id: sessionId, itemId } } });
      if (!response.ok && response.status !== 404) await load();
    } catch {
      await load();
    }
  }

  /** Sends a queued item now: into the running turn where the harness can take it, or as usual when idle. */
  async function sendNow(itemId: string): Promise<boolean> {
    error.value = undefined;
    try {
      const { error: body, response } = await api.POST("/api/sessions/{id}/queue/{itemId}/send", {
        params: { path: { id: sessionId, itemId } },
      });
      if (!response.ok) {
        error.value = errorMessage(body, `The message couldn't be sent (HTTP ${response.status}).`);
        await load();
        return false;
      }
      // Fleet took it out of the queue; its event says so too.
      queues[sessionId] = (queues[sessionId] ?? []).filter((item) => item.id !== itemId);
      return true;
    } catch (sendError) {
      error.value = sendError instanceof Error ? sendError.message : "The message couldn't be sent.";
      return false;
    }
  }

  return {
    queue,
    error: readonly(error),
    enqueue,
    remove,
    sendNow,
  };
}
