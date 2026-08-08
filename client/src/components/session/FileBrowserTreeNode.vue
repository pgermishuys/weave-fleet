<script setup lang="ts">
import { computed, inject } from 'vue'
import { ChevronRight, ChevronDown, Loader2 } from 'lucide-vue-next'
import type { BrowseDirectoryEntry } from '@/api/client'

interface Props {
  entry: BrowseDirectoryEntry
  depth: number
  sessionId: string
}

const props = defineProps<Props>()

// Inject the file browser composable from parent
const fileBrowser = inject<ReturnType<typeof import('@/composables/use-file-browser').useFileBrowser>>('fileBrowser')

if (!fileBrowser) {
  throw new Error('FileBrowserTreeNode must be used within a FileBrowserPanel')
}

const indentStyle = computed(() => ({
  paddingLeft: `${props.depth * 16}px`
}))

const isExpanded = computed(() => 
  fileBrowser?.isExpanded(props.entry.relativePath) ?? false
)

const isLoading = computed(() => 
  fileBrowser?.isLoading(props.entry.relativePath) ?? false
)

const children = computed(() => 
  fileBrowser?.expandedDirs.value.get(props.entry.relativePath) || []
)

async function toggleDirectory() {
  if (!fileBrowser) return
  if (isExpanded.value) {
    fileBrowser.collapseDirectory(props.entry.relativePath)
  } else {
    await fileBrowser.expandDirectory(props.entry.relativePath)
  }
}

async function handleFileClick() {
  if (!fileBrowser) return
  await fileBrowser.selectFile(props.entry.relativePath)
}
</script>

<template>
  <div class="file-browser-tree-node">
    <!-- Directory node -->
    <div
      v-if="entry.isDirectory"
      class="file-browser-tree-node__directory"
      :style="indentStyle"
      @click="toggleDirectory"
    >
      <ChevronDown v-if="isExpanded" class="file-browser-tree-node__chevron" :size="16" />
      <ChevronRight v-else class="file-browser-tree-node__chevron" :size="16" />
      <span class="file-browser-tree-node__name">{{ entry.name }}</span>
      <Loader2 
        v-if="isLoading" 
        class="file-browser-tree-node__spinner" 
        :size="14" 
      />
    </div>

    <!-- File node -->
    <div
      v-else
      class="file-browser-tree-node__file"
      :style="indentStyle"
      @click="handleFileClick"
    >
      <span class="file-browser-tree-node__file-dot" />
      <span class="file-browser-tree-node__name">{{ entry.name }}</span>
    </div>

    <!-- Recursive children -->
    <template v-if="entry.isDirectory && isExpanded">
      <div v-if="isLoading" class="file-browser-tree-node__loading" :style="{ paddingLeft: `${(depth + 1) * 16 + 8}px` }">
        <span class="file-browser-tree-node__loading-text">Loading...</span>
      </div>
      <template v-else-if="children.length > 0">
        <FileBrowserTreeNode
          v-for="child in children"
          :key="child.relativePath"
          :entry="child"
          :depth="depth + 1"
          :session-id="sessionId"
        />
      </template>
      <div v-else class="file-browser-tree-node__empty" :style="{ paddingLeft: `${(depth + 1) * 16 + 8}px` }">
        <span class="file-browser-tree-node__empty-text">No files</span>
      </div>
    </template>
  </div>
</template>

<style scoped>
.file-browser-tree-node {
  /* Container for node and children */
}

.file-browser-tree-node__directory,
.file-browser-tree-node__file {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 4px 8px;
  cursor: pointer;
  transition: background-color var(--transition);
  border-radius: 4px;
}

.file-browser-tree-node__directory:hover,
.file-browser-tree-node__file:hover {
  background-color: var(--bg);
}

.file-browser-tree-node__chevron {
  flex-shrink: 0;
  color: var(--muted);
  transition: transform var(--transition);
}

.file-browser-tree-node__spinner {
  flex-shrink: 0;
  color: var(--muted);
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

.file-browser-tree-node__file-dot {
  width: 8px;
  height: 8px;
  border-radius: 50%;
  flex-shrink: 0;
  background-color: var(--muted);
}

.file-browser-tree-node__name {
  color: var(--text);
  font-size: 14px;
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.file-browser-tree-node__loading,
.file-browser-tree-node__empty {
  padding: 4px 8px;
}

.file-browser-tree-node__loading-text,
.file-browser-tree-node__empty-text {
  color: var(--muted);
  font-size: 12px;
  font-style: italic;
}
</style>
