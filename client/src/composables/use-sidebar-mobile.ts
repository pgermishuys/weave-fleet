import { computed } from "vue";
import { storeToRefs } from "pinia";
import { useIsMobileNav } from "@/composables/use-media-query";
import { useSidebarStore } from "@/stores/sidebar";

/**
 * Combines sidebar store state with mobile breakpoint awareness.
 * - On mobile (≤716px): toggleSidebar opens/closes the mobile drawer
 * - On desktop (≥717px): toggleSidebar collapses/expands the inline panel
 * The right panel is a sheet over the conversation wherever it doesn't fit beside
 * it (always on mobile): show/hide open and close the sheet. Elsewhere they expand
 * and collapse the column.
 */
export function useSidebarMobile() {
  const sidebarStore = useSidebarStore();
  const {
    mobileDrawerOpen,
    panelCollapsed,
    rightPanelAsSheet,
    rightPanelCollapsed,
    rightPanelSheetOpen,
  } = storeToRefs(sidebarStore);
  const isMobileNav = useIsMobileNav();

  const isSidebarVisible = computed(() =>
    isMobileNav.value ? mobileDrawerOpen.value : !panelCollapsed.value,
  );

  const isRightPanelVisible = computed(() =>
    rightPanelAsSheet.value ? rightPanelSheetOpen.value : !rightPanelCollapsed.value,
  );

  function toggleSidebar(): void {
    if (isMobileNav.value) {
      sidebarStore.setMobileDrawerOpen(!mobileDrawerOpen.value);
    } else {
      sidebarStore.togglePanelCollapsed();
    }
  }

  function openDrawer(): void {
    sidebarStore.setMobileDrawerOpen(true);
  }

  function closeDrawer(): void {
    sidebarStore.setMobileDrawerOpen(false);
  }

  function showRightPanel(): void {
    if (rightPanelAsSheet.value) {
      sidebarStore.setRightPanelSheetOpen(true);
    } else {
      sidebarStore.setRightPanelCollapsed(false);
    }
  }

  function hideRightPanel(): void {
    if (rightPanelAsSheet.value) {
      sidebarStore.setRightPanelSheetOpen(false);
    } else {
      sidebarStore.setRightPanelCollapsed(true);
    }
  }

  function toggleRightPanel(): void {
    if (isRightPanelVisible.value) {
      hideRightPanel();
    } else {
      showRightPanel();
    }
  }

  return {
    isMobileNav,
    mobileDrawerOpen,
    panelCollapsed,
    isSidebarVisible,
    rightPanelAsSheet,
    isRightPanelVisible,
    toggleSidebar,
    openDrawer,
    closeDrawer,
    showRightPanel,
    hideRightPanel,
    toggleRightPanel,
  };
}
