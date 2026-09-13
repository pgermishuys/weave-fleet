import { beforeEach, describe, expect, it } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { useSidebarMobile } from "@/composables/use-sidebar-mobile";
import { useSidebarStore } from "@/stores/sidebar";

describe("useSidebarMobile right panel", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    localStorage.clear();
  });

  it("collapses and expands the column where it fits", () => {
    const store = useSidebarStore();
    const { isRightPanelVisible, hideRightPanel, showRightPanel, toggleRightPanel } = useSidebarMobile();

    expect(isRightPanelVisible.value).toBe(true);

    hideRightPanel();
    expect(store.rightPanelCollapsed).toBe(true);
    expect(isRightPanelVisible.value).toBe(false);

    showRightPanel();
    expect(store.rightPanelCollapsed).toBe(false);

    toggleRightPanel();
    expect(store.rightPanelCollapsed).toBe(true);
    expect(store.rightPanelSheetOpen).toBe(false);
  });

  it("opens and closes the sheet where it doesn't fit, without touching the column setting", () => {
    const store = useSidebarStore();
    store.setRightPanelCollapsed(true);
    store.setRightPanelAsSheet(true);
    const { isRightPanelVisible, hideRightPanel, showRightPanel, toggleRightPanel } = useSidebarMobile();

    expect(isRightPanelVisible.value).toBe(false);

    showRightPanel();
    expect(store.rightPanelSheetOpen).toBe(true);
    expect(isRightPanelVisible.value).toBe(true);

    hideRightPanel();
    expect(store.rightPanelSheetOpen).toBe(false);

    toggleRightPanel();
    expect(store.rightPanelSheetOpen).toBe(true);
    expect(store.rightPanelCollapsed).toBe(true);
  });

  it("starts with the sheet closed even when the column is open, and closes it when the column fits again", () => {
    const store = useSidebarStore();
    store.setRightPanelAsSheet(true);
    const { isRightPanelVisible, showRightPanel } = useSidebarMobile();

    expect(store.rightPanelCollapsed).toBe(false);
    expect(isRightPanelVisible.value).toBe(false);

    showRightPanel();
    store.setRightPanelAsSheet(false);

    expect(store.rightPanelSheetOpen).toBe(false);
    expect(isRightPanelVisible.value).toBe(true);
  });
});
