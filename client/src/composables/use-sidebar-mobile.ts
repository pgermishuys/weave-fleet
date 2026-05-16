import { computed } from "vue";
import { storeToRefs } from "pinia";
import { useIsMobileNav } from "@/composables/use-media-query";
import { useSidebarStore } from "@/stores/sidebar";

/**
 * Combines sidebar store state with mobile breakpoint awareness.
 * - On mobile (≤716px): toggleSidebar opens/closes the mobile drawer
 * - On desktop (≥717px): toggleSidebar collapses/expands the inline panel
 */
export function useSidebarMobile() {
  const sidebarStore = useSidebarStore();
  const { mobileDrawerOpen, panelCollapsed } = storeToRefs(sidebarStore);
  const isMobileNav = useIsMobileNav();

  const isSidebarVisible = computed(() =>
    isMobileNav.value ? mobileDrawerOpen.value : !panelCollapsed.value,
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

  return {
    isMobileNav,
    mobileDrawerOpen,
    panelCollapsed,
    isSidebarVisible,
    toggleSidebar,
    openDrawer,
    closeDrawer,
  };
}
