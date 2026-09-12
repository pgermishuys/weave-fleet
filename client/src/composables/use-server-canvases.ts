import { onBeforeUnmount, toValue, watch, type MaybeRefOrGetter } from "vue";
import { apiFetch } from "@/lib/api-client";
import { isCanvasEvent, type CanvasEvent } from "@/lib/domain-events";
import type { ServerCanvasSnapshot } from "@/lib/server-canvas";
import { useCanvasesStore } from "@/stores/canvases";
import { onReconnect, useWeaveSocket } from "@/composables/use-weave-socket";

function canvasesPath(sessionId: string): string {
  return `/api/sessions/${encodeURIComponent(sessionId)}/canvases`;
}

export async function fetchServerCanvases(sessionId: string): Promise<ServerCanvasSnapshot[]> {
  const response = await apiFetch(canvasesPath(sessionId));
  if (!response.ok) throw new Error(`HTTP ${response.status}`);
  const body: unknown = await response.json();
  return Array.isArray(body) ? (body as ServerCanvasSnapshot[]) : [];
}

/**
 * Close a server canvas: the tab goes at once, and the server sends
 * `canvas.closed`, which then finds nothing to remove. If the server refuses,
 * the session's canvases are loaded again so the tab comes back.
 */
export async function closeServerCanvas(sessionId: string, canvasId: string): Promise<void> {
  const store = useCanvasesStore();
  store.applyCanvasEvent({ type: "canvas.closed", payload: { sessionId, canvasId } });

  try {
    const response = await apiFetch(`${canvasesPath(sessionId)}/${encodeURIComponent(canvasId)}`, { method: "DELETE" });
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
  } catch (error) {
    console.warn(`Failed to close canvas ${canvasId}:`, error);
    try {
      store.setServerCanvases(sessionId, await fetchServerCanvases(sessionId));
    } catch {
      // The next session switch or reconnect loads them again.
    }
  }
}

/**
 * Keeps the active session's server canvases in the canvases store: loads
 * them when the session opens and on reconnect, and applies canvas events
 * from the session topic. Canvas events aren't replayed after a disconnect,
 * so a reconnect reloads the list.
 */
export function useServerCanvases(sessionId: MaybeRefOrGetter<string | null | undefined>): void {
  const store = useCanvasesStore();
  const { subscribeV2 } = useWeaveSocket();

  // Events that arrive while a list is loading are replayed on top of it,
  // because the list may have been read before them.
  let loading: { sessionId: string; buffered: CanvasEvent[] } | null = null;
  let loadId = 0;

  async function load(id: string): Promise<void> {
    const current = ++loadId;
    const pending = { sessionId: id, buffered: [] as CanvasEvent[] };
    loading = pending;

    try {
      const list = await fetchServerCanvases(id);
      if (current !== loadId) return;
      store.setServerCanvases(id, list);
      for (const event of pending.buffered) store.applyCanvasEvent(event);
    } catch (error) {
      console.warn(`Failed to load canvases for session ${id}:`, error);
    } finally {
      if (loading === pending) loading = null;
    }
  }

  watch(
    () => toValue(sessionId) ?? "",
    (id, _previous, onCleanup) => {
      if (!id) return;

      const unsubscribe = subscribeV2(
        `session:${id}`,
        () => {
          // Canvases load from the REST endpoint; snapshots don't carry them.
        },
        (event) => {
          if (!isCanvasEvent(event) || event.payload.sessionId !== id) return;
          store.applyCanvasEvent(event);
          if (loading?.sessionId === id) loading.buffered.push(event);
        },
      );
      void load(id);

      onCleanup(() => {
        unsubscribe();
        loadId += 1;
        loading = null;
      });
    },
    { immediate: true },
  );

  const stopReconnect = onReconnect(() => {
    const id = toValue(sessionId);
    if (id) void load(id);
  });

  onBeforeUnmount(stopReconnect);
}
