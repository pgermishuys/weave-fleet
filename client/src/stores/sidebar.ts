import { defineStore } from "pinia";
import { shallowRef } from "vue";

export type SidebarRail =
  | "board"
  | "sessions"
  | "analytics"
  | "github"
  | "marketplace"
  | "settings";

const LEFT_PANEL_STORAGE_KEY = "weave:left-collapsed";
const RIGHT_PANEL_STORAGE_KEY = "weave:right-collapsed";

function readStoredBoolean(key: string): boolean {
  if (typeof window === "undefined") {
    return false;
  }

  try {
    return window.localStorage.getItem(key) === "true";
  } catch {
    return false;
  }
}

function persistBoolean(key: string, value: boolean): void {
  if (typeof window === "undefined") {
    return;
  }

  try {
    window.localStorage.setItem(key, String(value));
  } catch {
    // localStorage unavailable
  }
}

export const useSidebarStore = defineStore("sidebar", () => {
  const activeRail = shallowRef<SidebarRail>("sessions");
  const panelCollapsed = shallowRef(readStoredBoolean(LEFT_PANEL_STORAGE_KEY));
  const rightPanelCollapsed = shallowRef(readStoredBoolean(RIGHT_PANEL_STORAGE_KEY));

  function setActiveRail(rail: SidebarRail): void {
    activeRail.value = rail;
  }

  function setPanelCollapsed(collapsed: boolean): void {
    panelCollapsed.value = collapsed;
    persistBoolean(LEFT_PANEL_STORAGE_KEY, collapsed);
  }

  function togglePanelCollapsed(): void {
    setPanelCollapsed(!panelCollapsed.value);
  }

  function setRightPanelCollapsed(collapsed: boolean): void {
    rightPanelCollapsed.value = collapsed;
    persistBoolean(RIGHT_PANEL_STORAGE_KEY, collapsed);
  }

  function toggleRightPanelCollapsed(): void {
    setRightPanelCollapsed(!rightPanelCollapsed.value);
  }

  return {
    activeRail,
    panelCollapsed,
    rightPanelCollapsed,
    setActiveRail,
    setPanelCollapsed,
    setRightPanelCollapsed,
    togglePanelCollapsed,
    toggleRightPanelCollapsed,
  };
});
