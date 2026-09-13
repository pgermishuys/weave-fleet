import { onBeforeUnmount, toValue, watch, type MaybeRefOrGetter } from "vue";
import { apiFetch } from "@/lib/api-client";
import { isAppEvent, isCanvasEvent, type AppUpdated, type CanvasEvent } from "@/lib/domain-events";
import type { ServerCanvasSnapshot } from "@/lib/server-canvas";
import { useAppRunsStore } from "@/stores/app-runs";
import { serverCanvasTabId, useCanvasesStore } from "@/stores/canvases";
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
 * Bring a server canvas forward: at once when its tab is open here, and through Fleet, which reopens it if it
 * was closed and tells every open client (`canvas.updated`, `canvas.focused`).
 */
export async function focusServerCanvas(sessionId: string, canvasId: string): Promise<void> {
  const store = useCanvasesStore();
  store.activate(sessionId, serverCanvasTabId(canvasId));

  try {
    const response = await apiFetch(`${canvasesPath(sessionId)}/${encodeURIComponent(canvasId)}/focus`, { method: "POST" });
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
  } catch (error) {
    console.warn(`Failed to focus canvas ${canvasId}:`, error);
  }
}

/**
 * Keeps the active session's server canvases in the canvases store: loads
 * them when the session opens and on reconnect, and applies canvas events
 * from the session topic. Canvas events aren't replayed after a disconnect,
 * so a reconnect reloads the list. It also passes `app.updated` to the
 * app-runs store, for the apps browser canvases show.
 */
export function useServerCanvases(sessionId: MaybeRefOrGetter<string | null | undefined>): void {
  const store = useCanvasesStore();
  const appRuns = useAppRunsStore();

  // Apps browser canvases show: the store keeps them current, and a page Fleet reloaded pulses its tabs.
  function applyAppEvent(event: AppUpdated): void {
    appRuns.applyEvent(event);
    if (event.payload.reason !== "reloaded") return;
    for (const canvas of store.sessionCanvases(event.payload.sessionId).canvases) {
      if (canvas.browser?.appId === event.payload.appId) store.markUpdated(canvas.id);
    }
  }
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
          if (isAppEvent(event) && event.payload.sessionId === id) {
            applyAppEvent(event);
            return;
          }
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
