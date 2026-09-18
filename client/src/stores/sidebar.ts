import { defineStore } from "pinia";
import { computed, shallowRef } from "vue";
import { useSessionsStore } from "@/stores/sessions";

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
const RIGHT_PANEL_BY_SESSION_STORAGE_KEY = "weave:right-collapsed-by-session";
// Oldest choices drop off past this, so the map doesn't grow with every session ever opened.
const RIGHT_PANEL_BY_SESSION_LIMIT = 200;

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

function readStoredSessionMap(): Record<string, boolean> {
  if (typeof window === "undefined") {
    return {};
  }

  try {
    const parsed: unknown = JSON.parse(window.localStorage.getItem(RIGHT_PANEL_BY_SESSION_STORAGE_KEY) ?? "{}");
    if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) return {};
    return Object.fromEntries(
      Object.entries(parsed).filter((entry): entry is [string, boolean] => typeof entry[1] === "boolean"),
    );
  } catch {
    return {};
  }
}

function persistSessionMap(map: Record<string, boolean>): void {
  if (typeof window === "undefined") {
    return;
  }

  try {
    window.localStorage.setItem(RIGHT_PANEL_BY_SESSION_STORAGE_KEY, JSON.stringify(map));
  } catch {
    // localStorage unavailable
  }
}

export const useSidebarStore = defineStore("sidebar", () => {
  const activeRail = shallowRef<SidebarRail>("sessions");
  const panelCollapsed = shallowRef(readStoredBoolean(LEFT_PANEL_STORAGE_KEY));
  const sessionsStore = useSessionsStore();
  // The last open/close choice anywhere: the board's panel, and the default for a session never toggled.
  const lastRightPanelCollapsed = shallowRef(readStoredBoolean(RIGHT_PANEL_STORAGE_KEY));
  // Each session remembers whether its panel was open, so switching sessions brings back that session's choice.
  const rightPanelCollapsedBySession = shallowRef(readStoredSessionMap());
  const rightPanelSessionId = computed(() =>
    activeRail.value === "sessions" ? sessionsStore.activeSessionId : null,
  );
  const rightPanelCollapsed = computed(() => {
    const sessionId = rightPanelSessionId.value;
    return (sessionId ? rightPanelCollapsedBySession.value[sessionId] : undefined) ?? lastRightPanelCollapsed.value;
  });
  const mobileDrawerOpen = shallowRef(false);
  // Where the conversation would be too narrow beside it (phones, narrow windows), the right
  // panel is a sheet over the conversation, closed until asked for. AppShell decides which.
  const rightPanelAsSheet = shallowRef(false);
  const rightPanelSheetOpen = shallowRef(false);
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
    lastRightPanelCollapsed.value = collapsed;
    persistBoolean(RIGHT_PANEL_STORAGE_KEY, collapsed);

    const sessionId = rightPanelSessionId.value;
    if (!sessionId) return;
    // Re-inserting moves the session to the newest end, so the limit drops the least recently toggled.
    const entries = Object.entries(rightPanelCollapsedBySession.value).filter(([id]) => id !== sessionId);
    entries.push([sessionId, collapsed]);
    const next = Object.fromEntries(entries.slice(-RIGHT_PANEL_BY_SESSION_LIMIT));
    rightPanelCollapsedBySession.value = next;
    persistSessionMap(next);
  }

  function toggleRightPanelCollapsed(): void {
    setRightPanelCollapsed(!rightPanelCollapsed.value);
  }

  function setMobileDrawerOpen(open: boolean): void {
    mobileDrawerOpen.value = open;
  }

  function setRightPanelAsSheet(asSheet: boolean): void {
    rightPanelAsSheet.value = asSheet;
    if (!asSheet) rightPanelSheetOpen.value = false;
  }

  function setRightPanelSheetOpen(open: boolean): void {
    rightPanelSheetOpen.value = open;
  }

  return {
    activeRail,
    panelCollapsed,
    rightPanelCollapsed,
    mobileDrawerOpen,
    rightPanelAsSheet,
    rightPanelSheetOpen,
    sessionListShown,
    registerSessionList,
    setActiveRail,
    setPanelCollapsed,
    setRightPanelCollapsed,
    setMobileDrawerOpen,
    setRightPanelAsSheet,
    setRightPanelSheetOpen,
    togglePanelCollapsed,
    toggleRightPanelCollapsed,
  };
});
