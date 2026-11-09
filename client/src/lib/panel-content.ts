import type { CanvasInstance } from "@/stores/canvases";

/** Tabs a session always has; anything else in the panel (a page, browser, visual, file, mod pane) is content. */
const ALWAYS_THERE = new Set(["changes", "files"]);

export function hasExtraTabs(canvases: readonly Pick<CanvasInstance, "kind">[]): boolean {
  return canvases.some((canvas) => !ALWAYS_THERE.has(canvas.kind));
}

export interface PanelContentState {
  changedFiles: number;
  canvases: readonly Pick<CanvasInstance, "kind">[];
  progress: boolean;
  context: boolean;
  agents: boolean;
}

/**
 * Does the session have something for the right panel to show? An empty session keeps the panel collapsed by
 * default; anything the user should see (changes, a tab the agent shows, a todo list, links, running agents) opens it.
 */
export function panelHasContent(state: PanelContentState): boolean {
  return state.changedFiles > 0 || hasExtraTabs(state.canvases) || state.progress || state.context || state.agents;
}
