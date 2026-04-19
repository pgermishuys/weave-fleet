<script setup lang="ts">
import { storeToRefs } from "pinia";
import CommandPalette from "@/components/CommandPalette.vue";
import TauriUpdateDialog from "@/components/TauriUpdateDialog.vue";
import CenterContent from "@/components/layout/CenterContent.vue";
import ContextPanel from "@/components/layout/ContextPanel.vue";
import IconRail from "@/components/layout/IconRail.vue";
import RightPanel from "@/components/layout/RightPanel.vue";
import TopBar from "@/components/layout/TopBar.vue";
import { useCommands } from "@/composables/use-commands";
import { useWeaveSocket } from "@/composables/use-weave-socket";
import { useSidebarStore } from "@/stores/sidebar";

useCommands();
useWeaveSocket();

const sidebarStore = useSidebarStore();
const { panelCollapsed } = storeToRefs(sidebarStore);
</script>

<template>
  <div class="app">
    <TopBar />

    <div class="main">
      <IconRail />
      <ContextPanel v-if="!panelCollapsed" />
      <CenterContent>
        <slot />
      </CenterContent>
      <RightPanel />
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
