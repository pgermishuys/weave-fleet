import { beforeEach, describe, expect, it } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { useSidebarStore } from "@/stores/sidebar";

describe("useSidebarStore", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
  });

  it("tracks the active rail and panel visibility", () => {
    const store = useSidebarStore();

    expect(store.activeRail).toBe("sessions");
    expect(store.panelCollapsed).toBe(false);

    store.setActiveRail("board");
    store.togglePanelCollapsed();

    expect(store.activeRail).toBe("board");
    expect(store.panelCollapsed).toBe(true);

    store.setPanelCollapsed(false);

    expect(store.panelCollapsed).toBe(false);
  });
});
