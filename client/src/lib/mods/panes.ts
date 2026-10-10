import { modPanes, modPaneOwner, type ModPaneView } from "@/lib/mods/points";
import { modPaneCanvasId, useCanvasesStore } from "@/stores/canvases";

/**
 * Open a mod's pane as a `mod` canvas in its session and bring it forward. A pane id has one tab per mod: opening it
 * again replaces the view the tab draws. To redraw a pane that is already open without pulling it forward, use
 * `updateModPane`.
 */
export function openModPane(view: ModPaneView): void {
  setView(view);
  useCanvasesStore().openModPane(view.sessionId, { paneId: view.paneId, title: view.title, mod: view.mod });
}

/** Replace an open pane's view and its tab's title, without opening it or changing the active tab. */
export function updateModPane(view: ModPaneView): void {
  setView(view);
  useCanvasesStore().updateModPane(view.sessionId, { paneId: view.paneId, title: view.title, mod: view.mod });
}

export function closeModPane(sessionId: string, mod: string, paneId: string): void {
  // Closing the tab drops the view too (the store does that wherever the tab is closed from).
  useCanvasesStore().close(sessionId, modPaneCanvasId(mod, paneId));
  modPanes.removeByOwner(modPaneOwner(sessionId, mod, paneId));
}

function setView(view: ModPaneView): void {
  const owner = modPaneOwner(view.sessionId, view.mod, view.paneId);
  modPanes.removeByOwner(owner);
  modPanes.contribute(owner, [view]);
}
