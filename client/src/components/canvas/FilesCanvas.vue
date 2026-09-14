<script setup lang="ts">
import { computed } from "vue";
import FileBrowserPanel from "@/components/session/FileBrowserPanel.vue";
import { provideContentPanelContext } from "@/composables/use-content-panel";

const props = defineProps<{
  sessionId: string;
}>();

// The tree fills the canvas; a file opens in its own tab.
const sessionIdRef = computed<string | null>(() => props.sessionId || null);
provideContentPanelContext(sessionIdRef);
</script>

<template>
  <div class="files-canvas">
    <FileBrowserPanel :session-id="sessionId" />
  </div>
</template>

<style scoped>
.files-canvas {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
  overflow-y: auto;
}
</style>
