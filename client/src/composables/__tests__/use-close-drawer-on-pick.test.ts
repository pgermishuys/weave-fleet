import { effectScope, nextTick, ref, type EffectScope } from "vue";
import { createPinia, setActivePinia } from "pinia";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { useCloseDrawerOnPick } from "@/composables/use-close-drawer-on-pick";
import { useAutomationsNav } from "@/composables/use-automations-nav";
import { useSettingsNav } from "@/composables/use-settings-nav";
import { useWorkflowsNav } from "@/composables/use-workflows-nav";
import { useSidebarStore } from "@/stores/sidebar";

describe("useCloseDrawerOnPick", () => {
  let scope: EffectScope;
  const pathname = ref("/");

  beforeEach(() => {
    setActivePinia(createPinia());
    localStorage.clear();
    pathname.value = "/";
    useSettingsNav().setActiveSection("general");
    useAutomationsNav().clearSelection();
    scope = effectScope();
    scope.run(() => useCloseDrawerOnPick(pathname));
  });

  afterEach(() => scope.stop());

  async function openThen(pick: () => void): Promise<boolean> {
    const store = useSidebarStore();
    store.setMobileDrawerOpen(true);
    await nextTick();
    pick();
    await nextTick();
    return store.mobileDrawerOpen;
  }

  it("closes the drawer when a session is picked (the route changes)", async () => {
    expect(await openThen(() => (pathname.value = "/sessions/invented-session"))).toBe(false);
  });

  it("closes the drawer when a Settings section is picked (no route change)", async () => {
    expect(await openThen(() => useSettingsNav().setActiveSection("machines"))).toBe(false);
  });

  it("closes the drawer when an automation is picked or a new one started", async () => {
    expect(await openThen(() => useAutomationsNav().setActiveAutomation("invented-automation"))).toBe(false);
    expect(await openThen(() => useAutomationsNav().startCreate())).toBe(false);
  });

  it("closes the drawer when a workflow is picked", async () => {
    const { activeWorkflowId } = useWorkflowsNav();
    expect(await openThen(() => (activeWorkflowId.value = "invented-workflow"))).toBe(false);
  });

  it("leaves the drawer open while nothing is picked", async () => {
    expect(await openThen(() => {})).toBe(true);
  });
});
