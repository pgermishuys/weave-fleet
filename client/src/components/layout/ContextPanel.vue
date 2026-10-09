<script setup lang="ts">
import type { Component } from "vue";
import { computed, shallowRef } from "vue";
import { storeToRefs } from "pinia";
import SessionsPanel from "@/components/sessions/SessionsPanel.vue";
import "@/components/layout/core-rails";
import { getRail } from "@/lib/rails";
import { useSidebarStore } from "@/stores/sidebar";

const sidebarStore = useSidebarStore();
const { activeRail } = storeToRefs(sidebarStore);

// A rail nobody has contributed (an id left over from a plugin that is gone) shows the sessions list.
const activeDefinition = computed(() => getRail(activeRail.value));

const activePanel = computed<Component>(() => activeDefinition.value?.panel ?? SessionsPanel);

const activePanelKey = computed(() => activeDefinition.value?.id ?? "sessions");

const props = defineProps<{
  /** Fill the container instead of the resizable width (the phone menu drawer). */
  fill?: boolean;
}>();

const MIN_WIDTH = 200;
const MAX_WIDTH = 500;
const panelWidth = shallowRef(280);
const isResizing = shallowRef(false);

function resizeBy(delta: number): void {
  panelWidth.value = Math.min(MAX_WIDTH, Math.max(MIN_WIDTH, panelWidth.value + delta));
}

defineExpose({ panelWidth, isResizing, resizeBy });
</script>

<template>
  <aside
    class="context-panel"
    :class="{ 'context-panel--fill': props.fill }"
    :style="props.fill ? undefined : { width: `${panelWidth}px`, minWidth: `${panelWidth}px` }"
    aria-label="Context panel"
  >
    <component
      :is="activePanel"
      :key="activePanelKey"
    />
  </aside>
</template>

<style scoped>
.context-panel {
  position: relative;
  background: transparent;
  display: flex;
  flex-direction: column;
  overflow: hidden;
}

.context-panel--fill {
  flex: 1;
  min-width: 0;
}

.context-panel :deep(.context-panel__content) {
  display: flex;
  flex: 1;
  flex-direction: column;
  justify-content: center;
  gap: 8px;
  min-height: 0;
  padding: 24px;
}

.context-panel :deep(.context-panel__eyebrow) {
  margin: 0;
  font-size: 10px;
  font-weight: 600;
  letter-spacing: 0.05em;
  text-transform: uppercase;
  color: var(--muted);
}

.context-panel :deep(.context-panel__title) {
  margin: 0;
  font-size: 18px;
  font-weight: 600;
  color: var(--text);
}

.context-panel :deep(.context-panel__description) {
  margin: 0;
  font-size: 13px;
  line-height: 1.5;
  color: var(--muted);
}
</style>
