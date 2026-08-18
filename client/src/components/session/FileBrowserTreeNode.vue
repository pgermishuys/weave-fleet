<script setup lang="ts">
import { computed, inject } from 'vue'
import { ChevronRight, ChevronDown, Loader2 } from 'lucide-vue-next'
import type { BrowseDirectoryEntry } from '@/api/client'
import type { UseDiffsResult } from '@/composables/use-diffs'
import { useContentPanelContext } from '@/composables/use-content-panel'

interface Props {
  entry: BrowseDirectoryEntry
  depth: number
  sessionId: string
}

const props = defineProps<Props>()

// Inject the file browser composable from parent
const fileBrowser = inject<ReturnType<typeof import('@/composables/use-file-browser').useFileBrowser>>('fileBrowser')
const diffs = inject<UseDiffsResult>('diffs')
const contentPanel = useContentPanelContext()

if (!fileBrowser) {
  throw new Error('FileBrowserTreeNode must be used within a FileBrowserPanel')
}

if (!diffs) {
  throw new Error('FileBrowserTreeNode requires diffs to be provided')
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

// Find diff info for this file
const diffInfo = computed(() => {
  if (props.entry.isDirectory) return null
  return diffs.diffs.value.find(d => d.file === props.entry.relativePath)
})

const statusBadge = computed(() => {
  const info = diffInfo.value
  if (!info) return null
  
  let statusLabel = ''
  let color = ''
  
  switch (info.status) {
    case 'added':
      statusLabel = 'added'
      color = 'green'
      break
    case 'modified':
      statusLabel = 'modified'
      color = 'orange'
      break
    case 'deleted':
      statusLabel = 'deleted'
      color = 'red'
      break
    default:
      return null
  }
  
  // Build aria-label
  const parts = [statusLabel]
  if (info.additions > 0) parts.push(`${info.additions} addition${info.additions === 1 ? '' : 's'}`)
  if (info.deletions > 0) parts.push(`${info.deletions} deletion${info.deletions === 1 ? '' : 's'}`)
  
  return {
    label: info.status === 'added' ? 'A' : info.status === 'modified' ? 'M' : 'D',
    color,
    ariaLabel: parts.join(', ')
  }
})

const lineCountText = computed(() => {
  const info = diffInfo.value
  if (!info) return null
  
  const parts: string[] = []
  if (info.additions > 0) parts.push(`+${info.additions}`)
  if (info.deletions > 0) parts.push(`-${info.deletions}`)
  
  return parts.length > 0 ? parts.join(' / ') : null
})

// Check if this file is currently selected
const isSelected = computed(() => {
  if (props.entry.isDirectory) return false
  return contentPanel.filesContext.value.selectedFilePath === props.entry.relativePath
})

// Filtered children based on All/Changed mode
const filteredChildren = computed(() => {
  const allChangedFilter = contentPanel.filesContext.value.allChangedFilter
  
  if (allChangedFilter === 'all') {
    return children.value
  }

  // In "changed" mode, filter children
  const changedPaths = new Set(diffs.diffs.value.map(d => d.file))
  
  // Build set of all ancestor directories needed
  const neededDirs = new Set<string>()
  for (const path of changedPaths) {
    let current = path
    while (current.includes('/')) {
      const parent = current.substring(0, current.lastIndexOf('/'))
      if (parent) {
        neededDirs.add(parent)
        current = parent
      } else {
        break
      }
    }
  }

  return children.value.filter(entry => {
    if (entry.isDirectory) {
      return neededDirs.has(entry.relativePath)
    } else {
      return changedPaths.has(entry.relativePath)
    }
  })
})

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
  // Call contentPanel.selectFile which updates state and switches to preview tab
  contentPanel.selectFile(props.entry.relativePath)
  // Then load the file content and show visual
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
      :class="{ 'file-browser-tree-node__file--selected': isSelected }"
      :style="indentStyle"
      @click="handleFileClick"
    >
      <span class="file-browser-tree-node__file-dot" />
      <span class="file-browser-tree-node__name">{{ entry.name }}</span>
      <div v-if="statusBadge" class="file-browser-tree-node__status" :aria-label="statusBadge.ariaLabel">
        <span 
          class="file-browser-tree-node__badge"
          :class="`file-browser-tree-node__badge--${statusBadge.color}`"
          aria-hidden="true"
        >
          {{ statusBadge.label }}
        </span>
        <span v-if="lineCountText" class="file-browser-tree-node__line-count" aria-hidden="true">
          {{ lineCountText }}
        </span>
      </div>
    </div>

    <!-- Recursive children -->
    <template v-if="entry.isDirectory && isExpanded">
      <div v-if="isLoading" class="file-browser-tree-node__loading" :style="{ paddingLeft: `${(depth + 1) * 16 + 8}px` }">
        <span class="file-browser-tree-node__loading-text">Loading...</span>
      </div>
      <template v-else-if="filteredChildren.length > 0">
        <FileBrowserTreeNode
          v-for="child in filteredChildren"
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

@media (prefers-reduced-motion: reduce) {
  .file-browser-tree-node__directory,
  .file-browser-tree-node__file {
    transition: none;
  }
}

.file-browser-tree-node__directory:hover,
.file-browser-tree-node__file:hover {
  background-color: var(--bg);
}

.file-browser-tree-node__file--selected {
  background-color: var(--primary);
  color: white;
}

.file-browser-tree-node__file--selected .file-browser-tree-node__name {
  color: white;
  font-weight: 600;
}

.file-browser-tree-node__file--selected .file-browser-tree-node__file-dot {
  background-color: white;
}

.file-browser-tree-node__file--selected:hover {
  background-color: var(--primary);
}

.file-browser-tree-node__chevron {
  flex-shrink: 0;
  color: var(--muted);
  transition: transform var(--transition);
}

@media (prefers-reduced-motion: reduce) {
  .file-browser-tree-node__chevron {
    transition: none;
  }
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

.file-browser-tree-node__status {
  display: flex;
  align-items: center;
  gap: 6px;
  margin-left: auto;
  flex-shrink: 0;
}

.file-browser-tree-node__badge {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 16px;
  height: 16px;
  border-radius: 2px;
  font-size: 10px;
  font-weight: 700;
  color: white;
}

.file-browser-tree-node__badge--green {
  background-color: #10b981;
}

.file-browser-tree-node__badge--orange {
  background-color: #f59e0b;
}

.file-browser-tree-node__badge--red {
  background-color: #ef4444;
}

.file-browser-tree-node__line-count {
  font-size: 11px;
  color: var(--muted);
  font-family: monospace;
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
