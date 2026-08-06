<script setup lang="ts">
import { computed } from 'vue'
import { useArtifactViewer } from '@/composables/use-artifact-viewer'

const { activeFilePath, viewMode } = useArtifactViewer()

const isRenderable = computed(() => {
  if (!activeFilePath.value) return false
  const ext = activeFilePath.value.slice(activeFilePath.value.lastIndexOf('.'))
  return ext === '.md' || ext === '.html'
})

function handleViewModeChange(mode: 'rendered' | 'source'): void {
  viewMode.value = mode
}
</script>

<template>
  <div class="artifact-viewer-toolbar">
    <div
      v-if="isRenderable"
      class="artifact-viewer-toolbar__toggle"
      role="group"
      aria-label="View mode"
    >
      <button
        type="button"
        class="artifact-viewer-toolbar__toggle-button"
        :class="{ active: viewMode === 'rendered' }"
        :aria-pressed="viewMode === 'rendered'"
        @click="handleViewModeChange('rendered')"
      >
        Rendered
      </button>
      <button
        type="button"
        class="artifact-viewer-toolbar__toggle-button"
        :class="{ active: viewMode === 'source' }"
        :aria-pressed="viewMode === 'source'"
        @click="handleViewModeChange('source')"
      >
        Source
      </button>
    </div>
  </div>
</template>

<style scoped>
.artifact-viewer-toolbar {
  display: flex;
  align-items: center;
  gap: 2px;
  padding: 8px;
  border-bottom: 1px solid var(--border);
  background: var(--bg);
}

.artifact-viewer-toolbar__toggle {
  display: flex;
  align-items: center;
  gap: 0;
  border: 1px solid var(--border);
}

.artifact-viewer-toolbar__toggle-button {
  padding: 4px 12px;
  border: none;
  background: transparent;
  color: var(--muted);
  font-size: 11px;
  cursor: pointer;
  transition: background-color var(--transition), color var(--transition);
}

.artifact-viewer-toolbar__toggle-button:hover {
  background: rgba(255, 255, 255, 0.05);
}

.artifact-viewer-toolbar__toggle-button:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -2px;
  z-index: 1;
}

.artifact-viewer-toolbar__toggle-button.active {
  background: var(--accent);
  color: var(--bg);
}

.artifact-viewer-toolbar__toggle-button + .artifact-viewer-toolbar__toggle-button {
  border-left: 1px solid var(--border);
}
</style>
