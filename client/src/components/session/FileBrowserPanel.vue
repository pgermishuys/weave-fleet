<script setup lang="ts">
import { computed, provide } from 'vue'
import { RotateCw, Loader2 } from 'lucide-vue-next'
import { useFileBrowser } from '@/composables/use-file-browser'
import FileBrowserTreeNode from './FileBrowserTreeNode.vue'

interface Props {
  sessionId: string
}

const props = defineProps<Props>()

const sessionIdRef = computed(() => props.sessionId)
const fileBrowser = useFileBrowser(sessionIdRef)

// Provide the composable to child components
provide('fileBrowser', fileBrowser)

const { rootEntries, rootLoading, error, refresh } = fileBrowser

async function handleRefresh() {
  await refresh()
}
</script>

<template>
  <div class="file-browser-panel">
    <div class="file-browser-panel__header">
      <span class="file-browser-panel__title">Files</span>
      <button
        class="file-browser-panel__refresh"
        :disabled="rootLoading"
        @click="handleRefresh"
        title="Refresh"
      >
        <RotateCw :size="14" :class="{ 'file-browser-panel__refresh-icon--spinning': rootLoading }" />
      </button>
    </div>

    <!-- Loading state -->
    <div v-if="rootLoading && rootEntries.length === 0" class="file-browser-panel__loading">
      <Loader2 class="file-browser-panel__spinner" :size="20" />
      <span class="file-browser-panel__loading-text">Loading files...</span>
    </div>

    <!-- Error state -->
    <div v-else-if="error" class="file-browser-panel__error">
      <p class="file-browser-panel__error-text">{{ error }}</p>
    </div>

    <!-- Empty state -->
    <div v-else-if="rootEntries.length === 0" class="file-browser-panel__empty">
      <p class="file-browser-panel__empty-text">No files found</p>
      <p class="file-browser-panel__empty-hint">
        The session directory is empty or not yet initialized.
      </p>
    </div>

    <!-- Tree -->
    <div v-else class="file-browser-panel__tree">
      <FileBrowserTreeNode
        v-for="entry in rootEntries"
        :key="entry.relativePath"
        :entry="entry"
        :depth="0"
        :session-id="sessionId"
      />
    </div>
  </div>
</template>

<style scoped>
.file-browser-panel {
  display: flex;
  flex-direction: column;
  gap: 12px;
  padding: 0;
}

.file-browser-panel__header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 8px 8px;
  border-bottom: 1px solid var(--border);
}

.file-browser-panel__title {
  font-size: 11px;
  font-weight: 600;
  color: var(--muted);
  text-transform: uppercase;
  letter-spacing: 0.5px;
}

.file-browser-panel__refresh {
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 4px;
  background: transparent;
  border: none;
  border-radius: 4px;
  cursor: pointer;
  color: var(--muted);
  transition: background-color var(--transition), color var(--transition);
}

.file-browser-panel__refresh:hover:not(:disabled) {
  background-color: var(--bg);
  color: var(--text);
}

.file-browser-panel__refresh:disabled {
  cursor: not-allowed;
  opacity: 0.5;
}

.file-browser-panel__refresh-icon--spinning {
  animation: spin 1s linear infinite;
}

@keyframes spin {
  from {
    transform: rotate(0deg);
  }
  to {
    transform: rotate(360deg);
  }
}

.file-browser-panel__loading {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 8px;
  padding: 24px 16px;
}

.file-browser-panel__spinner {
  color: var(--muted);
  animation: spin 1s linear infinite;
}

.file-browser-panel__loading-text {
  font-size: 12px;
  color: var(--muted);
}

.file-browser-panel__error {
  padding: 16px;
  background-color: rgba(239, 68, 68, 0.1);
  border-radius: 4px;
  margin: 0 8px;
}

.file-browser-panel__error-text {
  margin: 0;
  font-size: 13px;
  color: #ef4444;
}

.file-browser-panel__empty {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 24px 16px;
  text-align: center;
}

.file-browser-panel__empty-text {
  margin: 0;
  font-size: 13px;
  font-weight: 600;
  color: var(--text);
}

.file-browser-panel__empty-hint {
  margin: 0;
  font-size: 11px;
  line-height: 1.4;
  color: var(--muted);
}

.file-browser-panel__tree {
  display: flex;
  flex-direction: column;
}
</style>
