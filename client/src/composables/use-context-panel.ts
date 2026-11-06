import { computed, watch, type Component } from "vue";
import { useRouter } from "@tanstack/vue-router";
import { storeToRefs } from "pinia";
import "@/components/layout/core-rails";
import { getRail, NO_RAIL, railPanel } from "@/lib/rails";
import { useSidebarStore } from "@/stores/sidebar";

/**
 * What the active rail puts left of its page. Whatever is picked in the rail owns the whole context area, so this is
 * that rail's own menu or list, or null when the page fills the area (no rail owns the route, or the view is off).
 */
export function useContextPanel() {
  const { activeRail } = storeToRefs(useSidebarStore());
  const definition = computed(() => getRail(activeRail.value));
  const panel = computed<Component | null>(() => railPanel(definition.value));
  return { panel, panelKey: computed(() => definition.value?.id ?? NO_RAIL) };
}

/**
 * A rail nobody contributes (an id left from a plugin that is gone) opens Sessions. It never borrows the Sessions
 * panel to draw inside another view. Mount once, where the shell is.
 */
export function useStaleRailRedirect(): void {
  const sidebar = useSidebarStore();
  const { activeRail } = storeToRefs(sidebar);
  const router = useRouter({ warn: false });

  watch(
    [activeRail, () => getRail(activeRail.value) !== undefined],
    ([rail, known]) => {
      if (known || rail === NO_RAIL) return;
      sidebar.setActiveRail("sessions");
      void router?.navigate({ to: "/" });
    },
    { immediate: true },
  );
}
