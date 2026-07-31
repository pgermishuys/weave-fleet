<script setup lang="ts">
import { computed } from 'vue'
import { ArrowLeft } from 'lucide-vue-next'
import { useArtifactViewer } from '@/composables/use-artifact-viewer'
import { useSessionDiffsContext } from '@/composables/use-session-diffs-context'
import { Button } from '@/components/ui/button'

const { activeFilePath, viewMode, openFile, closeViewer } = useArtifactViewer()
const diffsContext = useSessionDiffsContext()

const allFiles = computed(() => {
  const diffs = diffsContext.value?.diffState.diffs.value
  if (!diffs) return []
  return diffs.map((diff) => diff.file)
})

const isRenderable = computed(() => {
  if (!activeFilePath.value) return false
  const ext = activeFilePath.value.slice(activeFilePath.value.lastIndexOf('.'))
  return ext === '.md' || ext === '.html'
})

function handleFileChange(event: Event): void {
  const target = event.target as HTMLSelectElement
  const path = target.value
  if (path) {
    openFile(path)
  }
}

function handleViewModeChange(mode: 'rendered' | 'source'): void {
  viewMode.value = mode
}
</script>

<template>
  <div class="artifact-viewer-toolbar">
    <Button
      variant="toolbar-icon"
      size="toolbar"
      title="Back to Artifacts"
      @click="closeViewer"
    >
      <ArrowLeft aria-hidden="true" />
    </Button>

    <span class="artifact-viewer-toolbar__divider" />

    <select
      :value="activeFilePath ?? ''"
      class="artifact-viewer-toolbar__file-picker"
      aria-label="Select file"
      @change="handleFileChange"
    >
      <option
        v-if="!activeFilePath"
        value=""
        disabled
      >
        Select a file
      </option>
      <option
        v-for="file in allFiles"
        :key="file"
        :value="file"
      >
        {{ file }}
      </option>
    </select>

    <span
      v-if="isRenderable"
      class="artifact-viewer-toolbar__divider"
    />

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

.artifact-viewer-toolbar__divider {
  width: 1px;
  height: 16px;
  margin-inline: 2px;
  background: var(--border);
}

.artifact-viewer-toolbar__file-picker {
  padding: 4px 8px;
  border: 1px solid var(--border);
  border-radius: 0;
  background: transparent;
  color: var(--text);
  font-size: 11px;
  cursor: pointer;
  transition: border-color var(--transition);
  max-width: 300px;
}

.artifact-viewer-toolbar__file-picker:hover {
  border-color: var(--accent);
}

.artifact-viewer-toolbar__file-picker:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
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
