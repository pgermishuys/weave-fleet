import { watch, type Ref } from "vue";
import { useAutomationsNav } from "@/composables/use-automations-nav";
import { useSettingsNav } from "@/composables/use-settings-nav";
import { useWorkflowsNav } from "@/composables/use-workflows-nav";
import { useSidebarStore } from "@/stores/sidebar";

/**
 * On a phone the context panel is a drawer over the page. Picking something in it (a session, a Settings section, an
 * automation, a workflow) shows that thing underneath, so the drawer closes. Some picks change the route; Settings,
 * Automations and Workflows keep theirs in local state, so those are watched too.
 */
export function useCloseDrawerOnPick(pathname: Readonly<Ref<string>>): void {
  const sidebarStore = useSidebarStore();
  const { activeSection, activeAnchor } = useSettingsNav();
  const { activeAutomationId, viewMode } = useAutomationsNav();
  const { activeWorkflowId } = useWorkflowsNav();

  watch([pathname, activeSection, activeAnchor, activeAutomationId, viewMode, activeWorkflowId], () => {
    if (sidebarStore.mobileDrawerOpen) sidebarStore.setMobileDrawerOpen(false);
  });
}
