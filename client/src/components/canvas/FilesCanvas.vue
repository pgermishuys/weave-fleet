<script setup lang="ts">
import { computed } from "vue";
import CanvasSplit from "@/components/canvas/CanvasSplit.vue";
import CanvasFileViewer from "@/components/canvas/CanvasFileViewer.vue";
import FileBrowserPanel from "@/components/session/FileBrowserPanel.vue";
import { provideContentPanelContext } from "@/composables/use-content-panel";

const props = defineProps<{
  sessionId: string;
}>();

const sessionIdRef = computed<string | null>(() => props.sessionId || null);
const contentPanel = provideContentPanelContext(sessionIdRef);
const selectedFilePath = computed(() => contentPanel.filesContext.value.selectedFilePath);
</script>

<template>
  <div class="files-canvas">
    <CanvasSplit :show-viewer="selectedFilePath !== null">
      <template #list>
        <FileBrowserPanel :session-id="sessionId" />
      </template>
      <template #viewer>
        <CanvasFileViewer />
      </template>
    </CanvasSplit>
  </div>
</template>

<style scoped>
.files-canvas {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
}
</style>
