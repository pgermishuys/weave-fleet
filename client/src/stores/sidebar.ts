import { defineStore } from "pinia";
import { shallowRef } from "vue";

export type SidebarRail =
  | "board"
  | "sessions"
  | "analytics"
  | "github"
  | "linear"
  | "slack"
  | "docker"
  | "sentry"
  | "marketplace"
  | "settings";

export const useSidebarStore = defineStore("sidebar", () => {
  const activeRail = shallowRef<SidebarRail>("sessions");
  const panelCollapsed = shallowRef(false);

  function setActiveRail(rail: SidebarRail): void {
    activeRail.value = rail;
  }

  function setPanelCollapsed(collapsed: boolean): void {
    panelCollapsed.value = collapsed;
  }

  function togglePanelCollapsed(): void {
    panelCollapsed.value = !panelCollapsed.value;
  }

  return {
    activeRail,
    panelCollapsed,
    setActiveRail,
    setPanelCollapsed,
    togglePanelCollapsed,
  };
});
