import { defineStore } from "pinia";
import { shallowRef } from "vue";
import type { CanvasEvent } from "@/lib/domain-events";
import { serverCanvasPayload, type ServerCanvasKind, type ServerCanvasSnapshot } from "@/lib/server-canvas";
import type { VisualPayload } from "@/lib/visual-payload";

/**
 * Session-scoped canvas state for the right panel.
 *
 * Each session keeps an ordered list of open canvases and the active one.
 * Changes and Files read the session's diffs and filesystem, and visual
 * canvases carry the payload they render. Server canvases mirror what the
 * server stores for the session: the agent changes them, and the user can
 * only close them.
 */

export type CanvasKind = "changes" | "files" | "visual";

/** Identifies a canvas the server stores. */
export interface ServerCanvasRef {
  canvasId: string;
  kind: ServerCanvasKind;
  version: number;
}

export interface CanvasInstance {
  id: string;
  kind: CanvasKind;
  /** Present on visual canvases: the diagram or document to render. */
  payload?: VisualPayload;
  /** Present on server canvases, which are visual canvases the agent keeps up to date. */
  server?: ServerCanvasRef;
}

export interface SessionCanvases {
  canvases: CanvasInstance[];
  activeId: string;
  /** Visual payloads that appeared in the conversation, oldest first. */
  knownVisuals: VisualPayload[];
}

const WIDENED_STORAGE_KEY = "weave:canvas-widened";

const FIXED_CANVAS_IDS = new Set(["changes"]);

function defaultSessionCanvases(): SessionCanvases {
  return {
    canvases: [
      { id: "changes", kind: "changes" },
      { id: "files", kind: "files" },
    ],
    activeId: "changes",
    knownVisuals: [],
  };
}

export function visualCanvasTitle(payload: VisualPayload): string {
  const title = payload.title?.trim();
  if (title) return title;

  const path = payload.sourceFilePath?.trim();
  if (path) return path.split("/").pop() || path;

  return "Diagram";
}

export function visualCanvasId(payload: VisualPayload): string {
  return `visual:${visualCanvasTitle(payload)}`;
}

export function serverCanvasTabId(canvasId: string): string {
  return `canvas:${canvasId}`;
}

function toServerCanvasInstance(canvas: ServerCanvasSnapshot): CanvasInstance | null {
  const payload = serverCanvasPayload(canvas);
  if (!payload) return null;

  return {
    id: serverCanvasTabId(canvas.canvasId),
    kind: "visual",
    payload,
    server: { canvasId: canvas.canvasId, kind: canvas.kind as ServerCanvasKind, version: canvas.version },
  };
}

function withoutCanvas(current: SessionCanvases, canvasId: string): SessionCanvases {
  const index = current.canvases.findIndex((canvas) => canvas.id === canvasId);
  if (index < 0) return current;

  const canvases = current.canvases.filter((canvas) => canvas.id !== canvasId);
  const activeId = current.activeId === canvasId
    ? (canvases[index - 1] ?? canvases[index] ?? canvases[0]).id
    : current.activeId;

  return { ...current, canvases, activeId };
}

export function isCanvasClosable(canvas: CanvasInstance): boolean {
  return !FIXED_CANVAS_IDS.has(canvas.id);
}

function readStoredBoolean(key: string): boolean {
  if (typeof window === "undefined") return false;

  try {
    return window.localStorage.getItem(key) === "true";
  } catch {
    return false;
  }
}

function persistBoolean(key: string, value: boolean): void {
  if (typeof window === "undefined") return;

  try {
    window.localStorage.setItem(key, String(value));
  } catch {
    // localStorage unavailable
  }
}

export const useCanvasesStore = defineStore("canvases", () => {
  const bySession = shallowRef<Record<string, SessionCanvases>>({});
  const widened = shallowRef(readStoredBoolean(WIDENED_STORAGE_KEY));

  function sessionCanvases(sessionId: string): SessionCanvases {
    return bySession.value[sessionId] ?? defaultSessionCanvases();
  }

  function update(sessionId: string, patch: (current: SessionCanvases) => SessionCanvases): void {
    bySession.value = {
      ...bySession.value,
      [sessionId]: patch(sessionCanvases(sessionId)),
    };
  }

  function activate(sessionId: string, canvasId: string): void {
    update(sessionId, (current) =>
      current.canvases.some((canvas) => canvas.id === canvasId)
        ? { ...current, activeId: canvasId }
        : current,
    );
  }

  /** Open (or focus) one of the built-in singleton canvases. */
  function open(sessionId: string, kind: Exclude<CanvasKind, "visual">): void {
    update(sessionId, (current) => {
      const exists = current.canvases.some((canvas) => canvas.id === kind);
      return {
        ...current,
        canvases: exists ? current.canvases : [...current.canvases, { id: kind, kind }],
        activeId: kind,
      };
    });
  }

  /**
   * Open a visual canvas for a payload. A canvas with the same title is
   * updated in place, so a revised diagram replaces its earlier version.
   */
  function openVisual(sessionId: string, payload: VisualPayload, options: { activate?: boolean } = {}): string {
    const id = visualCanvasId(payload);
    const shouldActivate = options.activate ?? true;

    update(sessionId, (current) => {
      const index = current.canvases.findIndex((canvas) => canvas.id === id);
      const next: CanvasInstance = { id, kind: "visual", payload };
      const canvases = index >= 0
        ? current.canvases.map((canvas, i) => (i === index ? next : canvas))
        : [...current.canvases, next];

      return {
        ...current,
        canvases,
        activeId: shouldActivate ? id : current.activeId,
      };
    });

    return id;
  }

  function close(sessionId: string, canvasId: string): void {
    if (FIXED_CANVAS_IDS.has(canvasId)) return;

    update(sessionId, (current) => withoutCanvas(current, canvasId));
  }

  /**
   * Replace the session's server canvases with a freshly loaded list, oldest
   * first. Other canvases keep their place; new server canvases go at the end.
   */
  function setServerCanvases(sessionId: string, list: ServerCanvasSnapshot[]): void {
    const loaded = new Map<string, CanvasInstance>();
    for (const item of list) {
      const instance = toServerCanvasInstance(item);
      if (instance) loaded.set(instance.id, instance);
    }

    update(sessionId, (current) => {
      const kept = current.canvases.flatMap((canvas) => {
        if (!canvas.server) return [canvas];
        const next = loaded.get(canvas.id);
        loaded.delete(canvas.id);
        return next ? [next] : [];
      });
      const canvases = [...kept, ...loaded.values()];
      const activeId = canvases.some((canvas) => canvas.id === current.activeId) ? current.activeId : canvases[0].id;

      return { ...current, canvases, activeId };
    });
  }

  /**
   * Apply a canvas event from the session topic. `canvas.updated` adds or
   * redraws the tab without taking focus, `canvas.focused` brings it forward,
   * and `canvas.closed` removes it (a no-op when this tab closed it already).
   */
  function applyCanvasEvent(event: CanvasEvent): void {
    const { sessionId } = event.payload;
    const tabId = serverCanvasTabId(event.payload.canvasId);

    switch (event.type) {
      case "canvas.updated": {
        const next = toServerCanvasInstance(event.payload);
        if (!next) return;

        update(sessionId, (current) => {
          const index = current.canvases.findIndex((canvas) => canvas.id === tabId);
          if (index < 0) return { ...current, canvases: [...current.canvases, next] };

          // Events can overtake a list that was loaded earlier; never go back a version.
          const existing = current.canvases[index].server;
          if (existing && existing.version > event.payload.version) return current;

          return { ...current, canvases: current.canvases.map((canvas, i) => (i === index ? next : canvas)) };
        });
        return;
      }
      case "canvas.focused":
        activate(sessionId, tabId);
        return;
      case "canvas.closed":
        update(sessionId, (current) => withoutCanvas(current, tabId));
        return;
    }
  }

  function setKnownVisuals(sessionId: string, payloads: VisualPayload[]): void {
    update(sessionId, (current) => ({ ...current, knownVisuals: payloads }));
  }

  function setWidened(value: boolean): void {
    widened.value = value;
    persistBoolean(WIDENED_STORAGE_KEY, value);
  }

  function toggleWidened(): void {
    setWidened(!widened.value);
  }

  return {
    bySession,
    widened,
    sessionCanvases,
    activate,
    open,
    openVisual,
    close,
    setServerCanvases,
    applyCanvasEvent,
    setKnownVisuals,
    setWidened,
    toggleWidened,
  };
});
