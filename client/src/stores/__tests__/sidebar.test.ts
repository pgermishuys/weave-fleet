import { beforeEach, describe, expect, it } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { useSidebarStore } from "@/stores/sidebar";

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
});
