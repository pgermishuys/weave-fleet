<script setup lang="ts">
import { computed } from "vue";
import { useLocation } from "@tanstack/vue-router";
import { storeToRefs } from "pinia";
import CommandPalette from "@/components/CommandPalette.vue";
import TauriUpdateDialog from "@/components/TauriUpdateDialog.vue";
import BoardRightPanel from "@/components/board/BoardRightPanel.vue";
import CenterContent from "@/components/layout/CenterContent.vue";
import ContextPanel from "@/components/layout/ContextPanel.vue";
import IconRail from "@/components/layout/IconRail.vue";
import SessionsV1RightPanel from "@/components/sessions-v1/SessionsV1RightPanel.vue";
import SessionsV2RightPanel from "@/components/sessions/SessionsV2RightPanel.vue";
import { useCommands } from "@/composables/use-commands";
import { useWeaveSocket } from "@/composables/use-weave-socket";
import { useSidebarStore } from "@/stores/sidebar";

useCommands();
useWeaveSocket();

const pathname = useLocation({
  select: (location) => location.pathname,
});
const sidebarStore = useSidebarStore();

const { panelCollapsed, activeRail } = storeToRefs(sidebarStore);

const isSettingsRoute = computed(() => pathname.value.startsWith("/settings"));

const showSessionsV2Panel = computed(() =>
  !isSettingsRoute.value && (activeRail.value === "sessions" || activeRail.value === "analytics"),
);

const showSessionsV1Panel = computed(() =>
  !isSettingsRoute.value && activeRail.value === "sessions-v1",
);

const showBoardPanel = computed(() =>
  !isSettingsRoute.value && activeRail.value === "board",
);
</script>

<template>
  <div class="app">
    <div class="main">
      <IconRail />
      <ContextPanel v-if="!panelCollapsed" />
      <CenterContent>
        <slot />
      </CenterContent>

      <SessionsV2RightPanel v-if="showSessionsV2Panel" />
      <SessionsV1RightPanel v-else-if="showSessionsV1Panel" />
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
  height: 100vh;
}

.main {
  display: flex;
  flex: 1;
  overflow: hidden;
}
</style>
