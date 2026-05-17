<script setup lang="ts">
import { computed, watch } from "vue";
import { useLocation } from "@tanstack/vue-router";
import { storeToRefs } from "pinia";
import CommandPalette from "@/components/CommandPalette.vue";
import TauriUpdateDialog from "@/components/TauriUpdateDialog.vue";
import BoardRightPanel from "@/components/board/BoardRightPanel.vue";
import CenterContent from "@/components/layout/CenterContent.vue";
import ContextPanel from "@/components/layout/ContextPanel.vue";
import IconRail from "@/components/layout/IconRail.vue";
import SessionsV2RightPanel from "@/components/sessions/SessionsV2RightPanel.vue";
import { Sheet, SheetContent } from "@/components/ui/sheet";
import { useCommands } from "@/composables/use-commands";
import { useWeaveSocket } from "@/composables/use-weave-socket";
import { useSidebarMobile } from "@/composables/use-sidebar-mobile";
import { useVisualViewport } from "@/composables/use-visual-viewport";
import { useKeyboardScroll } from "@/composables/use-keyboard-scroll";
import { useFoldableScreen } from "@/composables/use-foldable-screen";
import { useSidebarStore } from "@/stores/sidebar";

useCommands();
useWeaveSocket();
useVisualViewport();
useKeyboardScroll();

const foldable = useFoldableScreen();

// Keep --fold-gap CSS property in sync with foldable screen hinge width
watch(foldable, ({ foldWidth }) => {
  document.documentElement.style.setProperty("--fold-gap", `${foldWidth}px`);
}, { immediate: true });

const pathname = useLocation({
  select: (location) => location.pathname,
});
const sidebarStore = useSidebarStore();
const { isMobileNav, mobileDrawerOpen, closeDrawer } = useSidebarMobile();

const { panelCollapsed, activeRail } = storeToRefs(sidebarStore);

const isSettingsRoute = computed(() => pathname.value.startsWith("/settings"));

const showSessionsV2Panel = computed(() =>
  !isSettingsRoute.value && (activeRail.value === "sessions" || activeRail.value === "analytics"),
);

const showBoardPanel = computed(() =>
  !isSettingsRoute.value && activeRail.value === "board",
);

// Touch swipe support: swipe right from left edge to open drawer
let touchStartX = 0;
let touchStartY = 0;

function onTouchStart(e: TouchEvent): void {
  if (!isMobileNav.value || mobileDrawerOpen.value) {
    return;
  }

  const touch = e.touches[0];

  if (!touch) {
    return;
  }

  if (touch.clientX <= 24) {
    touchStartX = touch.clientX;
    touchStartY = touch.clientY;
  } else {
    touchStartX = -1;
    touchStartY = -1;
  }
}

function onTouchEnd(e: TouchEvent): void {
  if (!isMobileNav.value || mobileDrawerOpen.value || touchStartX < 0) {
    return;
  }

  const touch = e.changedTouches[0];

  if (!touch) {
    return;
  }

  const dx = touch.clientX - touchStartX;
  const dy = Math.abs(touch.clientY - touchStartY);

  if (dx >= 50 && dy < 60) {
    sidebarStore.setMobileDrawerOpen(true);
  }

  touchStartX = -1;
  touchStartY = -1;
}
</script>

<template>
  <div
    class="app"
    @touchstart.passive="onTouchStart"
    @touchend.passive="onTouchEnd"
  >
    <!-- Mobile: Sheet drawer for nav -->
    <Sheet
      v-if="isMobileNav"
      :open="mobileDrawerOpen"
      @update:open="(v) => !v && closeDrawer()"
    >
      <SheetContent
        side="left"
        class="w-[280px] p-0 gap-0"
      >
        <div class="flex h-full">
          <IconRail />
          <ContextPanel />
        </div>
      </SheetContent>
    </Sheet>

    <div
      class="main"
      :class="{ 'fold-gap': foldable.isFolded }"
    >
      <!-- Desktop: inline nav -->
      <template v-if="!isMobileNav">
        <IconRail />
        <ContextPanel v-if="!panelCollapsed" />
      </template>

      <CenterContent>
        <slot />
      </CenterContent>

      <SessionsV2RightPanel v-if="showSessionsV2Panel" />
      <BoardRightPanel v-else-if="showBoardPanel" />
    </div>

    <CommandPalette />
    <TauriUpdateDialog />
  </div>
</template>

<style scoped>
.app {
  display: flex;
  flex-direction: column;
  height: var(--visual-vh, 100dvh);
}

.main {
  display: flex;
  flex: 1;
  overflow: hidden;
}
</style>
