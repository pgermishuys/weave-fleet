import { beforeEach, describe, expect, it } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { useSessionsStore } from "@/stores/sessions";
import { machineGroupKey, projectGroupKey, useSidebarStore } from "@/stores/sidebar";

describe("useSidebarStore", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    localStorage.clear();
  });

  it("tracks the active rail and panel visibility", () => {
    const store = useSidebarStore();

    expect(store.activeRail).toBe("sessions");
    expect(store.panelCollapsed).toBe(false);
    expect(store.rightPanelCollapsed).toBe(false);

    store.setActiveRail("board");
    store.togglePanelCollapsed();
    store.toggleRightPanelCollapsed();

    expect(store.activeRail).toBe("board");
    expect(store.panelCollapsed).toBe(true);
    expect(store.rightPanelCollapsed).toBe(true);

    store.setPanelCollapsed(false);
    store.setRightPanelCollapsed(false);

    expect(store.panelCollapsed).toBe(false);
    expect(store.rightPanelCollapsed).toBe(false);
  });

  it("persists both panel states", () => {
    const store = useSidebarStore();

    store.setPanelCollapsed(true);
    store.setRightPanelCollapsed(true);

    expect(localStorage.getItem("weave:left-collapsed")).toBe("true");
    expect(localStorage.getItem("weave:right-collapsed")).toBe("true");

    setActivePinia(createPinia());

    const rehydratedStore = useSidebarStore();

    expect(rehydratedStore.panelCollapsed).toBe(true);
    expect(rehydratedStore.rightPanelCollapsed).toBe(true);
  });

  it("remembers which session-list groups are collapsed across a reload", () => {
    const store = useSidebarStore();
    const machine = machineGroupKey("mac");
    const project = projectGroupKey("mac", "p1");

    expect(store.isGroupCollapsed(machine)).toBe(false);

    store.toggleGroupCollapsed(machine);
    store.setGroupCollapsed(project, true);
    store.setGroupCollapsed(projectGroupKey("mac", "p2"), true);
    store.setGroupCollapsed(projectGroupKey("mac", "p2"), false);

    setActivePinia(createPinia());
    const rehydrated = useSidebarStore();

    expect(rehydrated.isGroupCollapsed(machine)).toBe(true);
    expect(rehydrated.isGroupCollapsed(project)).toBe(true);
    expect(rehydrated.isGroupCollapsed(projectGroupKey("mac", "p2"))).toBe(false);
    // The same project id on another machine is its own group.
    expect(rehydrated.isGroupCollapsed(projectGroupKey("linux", "p1"))).toBe(false);
    // Only collapsed groups are kept.
    expect(JSON.parse(localStorage.getItem("weave:sessions-collapsed-groups") ?? "{}")).toEqual({
      [machine]: true,
      [project]: true,
    });
  });

  it("remembers the right panel per session", () => {
    const store = useSidebarStore();
    const sessions = useSessionsStore();

    sessions.setActiveSessionId("a");
    store.setRightPanelCollapsed(true);
    sessions.setActiveSessionId("b");
    store.setRightPanelCollapsed(false);

    sessions.setActiveSessionId("a");
    expect(store.rightPanelCollapsed).toBe(true);
    sessions.setActiveSessionId("b");
    expect(store.rightPanelCollapsed).toBe(false);

    // A session never toggled takes the last choice made anywhere.
    sessions.setActiveSessionId("c");
    expect(store.rightPanelCollapsed).toBe(false);

    setActivePinia(createPinia());
    const rehydrated = useSidebarStore();
    useSessionsStore().setActiveSessionId("a");
    expect(rehydrated.rightPanelCollapsed).toBe(true);
  });

  it("keeps the board's right panel out of the session choices", () => {
    const store = useSidebarStore();
    const sessions = useSessionsStore();

    sessions.setActiveSessionId("a");
    store.setRightPanelCollapsed(true);

    store.setActiveRail("board");
    expect(store.rightPanelCollapsed).toBe(true);
    store.setRightPanelCollapsed(false);
    expect(store.rightPanelCollapsed).toBe(false);

    store.setActiveRail("sessions");
    expect(store.rightPanelCollapsed).toBe(true);
  });

  it("knows whether any sessions list is mounted", () => {
    const store = useSidebarStore();
    expect(store.sessionListShown).toBe(false);

    const releaseDesktop = store.registerSessionList();
    const releaseDrawer = store.registerSessionList();
    releaseDesktop();
    releaseDesktop();
    expect(store.sessionListShown).toBe(true);

    releaseDrawer();
    expect(store.sessionListShown).toBe(false);
  });
});
