import type { Pinia } from "pinia";
import { closeModPane, openModPane, updateModPane } from "@/lib/mods/panes";
import { composerBands, modPanes, statusChips, toolRowViews } from "@/lib/mods/points";
import { setActivePinia } from "pinia";

/**
 * How Playwright contributes fake trees in mock mode, for screenshots and checks. Dev and mock builds only; a
 * production build doesn't expose it.
 */
export function installModsTestApi(pinia?: Pinia): void {
  if (typeof window === "undefined") return;
  if (!(import.meta.env.DEV || import.meta.env.MODE === "mock")) return;
  if (pinia) setActivePinia(pinia);

  window.__FLEET_MODS_TEST_API = {
    toolRowViews,
    composerBands,
    statusChips,
    modPanes,
    openModPane,
    updateModPane,
    closeModPane,
    clear() {
      toolRowViews.clear();
      composerBands.clear();
      statusChips.clear();
      modPanes.clear();
    },
  };
}

declare global {
  interface Window {
    __FLEET_MODS_TEST_API?: {
      toolRowViews: typeof toolRowViews;
      composerBands: typeof composerBands;
      statusChips: typeof statusChips;
      modPanes: typeof modPanes;
      openModPane: typeof openModPane;
      updateModPane: typeof updateModPane;
      closeModPane: typeof closeModPane;
      clear: () => void;
    };
  }
}
