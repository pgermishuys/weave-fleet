import { defineStore } from "pinia";
import { computed, shallowRef } from "vue";

export type SidebarRail =
  | "board"
  | "sessions"
  | "analytics"
  | "automations"
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
  const mobileDrawerOpen = shallowRef(false);
  // Session rows are the only place a session's status shows. Each mounted
  // sessions list registers here so the header can show the status when no
  // list is on screen (panel collapsed, another rail open, mobile drawer shut).
  const mountedSessionLists = shallowRef(0);
  const sessionListShown = computed(() => mountedSessionLists.value > 0);

  function registerSessionList(): () => void {
    mountedSessionLists.value += 1;
    let released = false;
    return () => {
      if (released) return;
      released = true;
      mountedSessionLists.value -= 1;
    };
  }

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

  function setMobileDrawerOpen(open: boolean): void {
    mobileDrawerOpen.value = open;
  }

  return {
    activeRail,
    panelCollapsed,
    rightPanelCollapsed,
    mobileDrawerOpen,
    sessionListShown,
    registerSessionList,
    setActiveRail,
    setPanelCollapsed,
    setRightPanelCollapsed,
    setMobileDrawerOpen,
    togglePanelCollapsed,
    toggleRightPanelCollapsed,
  };
});
