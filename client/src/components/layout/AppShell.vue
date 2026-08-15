<!--
Panel Vocabulary:
  rail         — Always-visible icon navigation (left edge)
  session-list — List of sessions
  conversation — Message/activity stream
  content      — Right-side artifact viewer
-->
<script setup lang="ts">
import { computed, ref, shallowRef, watch } from "vue";
import { useLocation } from "@tanstack/vue-router";
import { storeToRefs } from "pinia";
import CommandPalette from "@/components/CommandPalette.vue";
import TauriUpdateDialog from "@/components/TauriUpdateDialog.vue";
import BoardRightPanel from "@/components/board/BoardRightPanel.vue";
import CenterContent from "@/components/layout/CenterContent.vue";
import ContextPanel from "@/components/layout/ContextPanel.vue";
import IconRail from "@/components/layout/IconRail.vue";
import StatusBar from "@/components/layout/StatusBar.vue";
import SessionsV2RightPanel from "@/components/sessions/SessionsV2RightPanel.vue";
import { Menu } from "lucide-vue-next";
import { Sheet, SheetContent } from "@/components/ui/sheet";
import { Button } from "@/components/ui/button";
import { useCommands } from "@/composables/use-commands";
import { useWeaveSocket } from "@/composables/use-weave-socket";
import { useSessionActivityUpdates } from "@/composables/use-session-activity-updates";
import { useSidebarMobile } from "@/composables/use-sidebar-mobile";
import { useVisualViewport } from "@/composables/use-visual-viewport";
import { useKeyboardScroll } from "@/composables/use-keyboard-scroll";
import { useFoldableScreen } from "@/composables/use-foldable-screen";
import { useBoardFeature } from "@/composables/use-board-feature";
import { useSidebarStore } from "@/stores/sidebar";

useCommands();
useWeaveSocket();
useSessionActivityUpdates();
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
const { isMobileNav, mobileDrawerOpen, openDrawer, closeDrawer } = useSidebarMobile();
const { isBoardFeatureEnabled } = useBoardFeature();

const { panelCollapsed, activeRail } = storeToRefs(sidebarStore);

const isSettingsRoute = computed(() => pathname.value.startsWith("/settings"));

const showSessionsV2Panel = computed(() =>
  !isSettingsRoute.value && (activeRail.value === "sessions" || activeRail.value === "analytics"),
);

const showBoardPanel = computed(() =>
  isBoardFeatureEnabled.value && !isSettingsRoute.value && activeRail.value === "board",
);

const showRightPanel = computed(() => showSessionsV2Panel.value || showBoardPanel.value);

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

// --- Resize gutter logic ---
const rightPanelWidth = ref(360);
const isGutterDragging = ref(false);

// --- Left gutter (context panel ↔ conversation) ---
const contextPanelRef = shallowRef<InstanceType<typeof ContextPanel> | null>(null);
const isLeftGutterDragging = ref(false);

function onLeftGutterPointerDown(e: PointerEvent): void {
  const panel = contextPanelRef.value;
  if (!panel) return;

  isLeftGutterDragging.value = true;
  panel.isResizing = true;
  const startX = e.clientX;
  const startWidth = panel.panelWidth;
  document.body.style.cursor = "col-resize";
  document.body.style.userSelect = "none";

  const onMove = (ev: PointerEvent) => {
    const delta = ev.clientX - startX;
    panel.panelWidth = Math.min(500, Math.max(200, startWidth + delta));
  };

  const onUp = () => {
    isLeftGutterDragging.value = false;
    panel.isResizing = false;
    document.body.style.cursor = "";
    document.body.style.userSelect = "";
    document.removeEventListener("pointermove", onMove);
    document.removeEventListener("pointerup", onUp);
  };

  document.addEventListener("pointermove", onMove);
  document.addEventListener("pointerup", onUp);
}

function onGutterPointerDown(e: PointerEvent): void {
  isGutterDragging.value = true;
  const startX = e.clientX;
  const startWidth = rightPanelWidth.value;
  document.body.style.cursor = "col-resize";
  document.body.style.userSelect = "none";

  const onMove = (e: PointerEvent) => {
    const delta = startX - e.clientX;
    const mainEl = document.querySelector(".main");
    const appWidth = mainEl?.getBoundingClientRect().width ?? window.innerWidth;
    const maxWidth = appWidth * 0.5;
    rightPanelWidth.value = Math.max(200, Math.min(maxWidth, startWidth + delta));
  };

  const onUp = () => {
    isGutterDragging.value = false;
    document.body.style.cursor = "";
    document.body.style.userSelect = "";
    document.removeEventListener("pointermove", onMove);
    document.removeEventListener("pointerup", onUp);
  };

  document.addEventListener("pointermove", onMove);
  document.addEventListener("pointerup", onUp);
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

    <!-- Mobile: hamburger menu button -->
    <Button
      v-if="isMobileNav"
      variant="toolbar-icon"
      size="toolbar"
      class="mobile-menu-btn"
      :aria-label="mobileDrawerOpen ? 'Close menu' : 'Open menu'"
      :aria-expanded="mobileDrawerOpen"
      @click="openDrawer"
    >
      <Menu class="h-5 w-5" />
    </Button>

    <div
      class="main"
      :class="{ 'fold-gap': foldable.isFolded }"
    >
      <!-- Desktop: inline nav -->
      <template v-if="!isMobileNav">
        <IconRail />
        <ContextPanel v-if="!panelCollapsed" ref="contextPanelRef" />
        <div
          v-if="!panelCollapsed"
          class="resize-gutter"
          :class="{ active: isLeftGutterDragging }"
          @pointerdown.prevent="onLeftGutterPointerDown"
        />
      </template>

      <CenterContent>
        <slot />
      </CenterContent>

      <div
        v-if="showRightPanel"
        class="resize-gutter"
        :class="{ active: isGutterDragging }"
        @pointerdown.prevent="onGutterPointerDown"
      />

      <SessionsV2RightPanel
        v-if="showSessionsV2Panel"
        :width="rightPanelWidth"
      />
      <BoardRightPanel
        v-else-if="showBoardPanel"
        :width="rightPanelWidth"
      />
    </div>

    <StatusBar />

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
  gap: 8px;
  padding: 8px;
  background: var(--main-bg);
}

.mobile-menu-btn {
  position: fixed;
  top: 8px;
  left: 8px;
  z-index: 10;
}

.resize-gutter {
  width: 8px;
  margin: 0 -4px;
  flex-shrink: 0;
  cursor: col-resize;
  background: transparent;
  transition: background var(--transition);
  z-index: 5;
}

.resize-gutter:hover,
.resize-gutter.active {
  background: rgba(91, 110, 199, 0.15);
}
</style>
