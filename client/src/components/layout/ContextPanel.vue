<script setup lang="ts">
import { shallowRef } from "vue";
import { useContextPanel } from "@/composables/use-context-panel";

const { panel: activePanel, panelKey: activePanelKey } = useContextPanel();

const props = defineProps<{
  /** Fill the container instead of the resizable width (the phone menu drawer). */
  fill?: boolean;
}>();

const MIN_WIDTH = 200;
const MAX_WIDTH = 500;
const panelWidth = shallowRef(306);
const isResizing = shallowRef(false);

function resizeBy(delta: number): void {
  panelWidth.value = Math.min(MAX_WIDTH, Math.max(MIN_WIDTH, panelWidth.value + delta));
}

defineExpose({ panelWidth, isResizing, resizeBy });
</script>

<template>
  <aside
    v-if="activePanel"
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
  font-size: 0.71429rem;
  font-weight: 600;
  letter-spacing: 0.05em;
  text-transform: uppercase;
  color: var(--muted);
}

.context-panel :deep(.context-panel__title) {
  margin: 0;
  font-size: 1.2857rem;
  font-weight: 600;
  color: var(--text);
}

.context-panel :deep(.context-panel__description) {
  margin: 0;
  font-size: 0.92857rem;
  line-height: 1.5;
  color: var(--muted);
}
</style>
