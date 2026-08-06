<script setup lang="ts">
import { ref, computed } from 'vue'
import { ChevronRight, ChevronDown } from 'lucide-vue-next'
import type { ArtifactTreeNode } from '@/lib/artifact-tree'

interface Props {
  node: ArtifactTreeNode
  depth: number
}

const props = defineProps<Props>()

const emit = defineEmits<{
  selectFile: [path: string]
}>()

const isExpanded = ref(true)

const indentStyle = computed(() => ({
  paddingLeft: `${props.depth * 16}px`
}))

const statusColor = computed(() => {
  switch (props.node.status) {
    case 'added':
      return '#10b981'
    case 'modified':
      return '#3b82f6'
    case 'deleted':
      return '#6b7280'
    default:
      return '#6b7280'
  }
})

const statText = computed(() => {
  if (!props.node.status) return ''
  
  if (props.node.status === 'added') {
    const lines = (props.node.additions ?? 0) + (props.node.deletions ?? 0)
    return `${lines} lines`
  }
  
  if (props.node.status === 'modified') {
    const additions = props.node.additions ?? 0
    const deletions = props.node.deletions ?? 0
    return `+${additions}/-${deletions}`
  }
  
  if (props.node.status === 'deleted') {
    return 'read'
  }
  
  return ''
})

function toggleExpand() {
  isExpanded.value = !isExpanded.value
}

function handleFileClick() {
  emit('selectFile', props.node.fullPath)
}

function handleChildSelectFile(path: string) {
  emit('selectFile', path)
}
</script>

<template>
  <div class="artifacts-tree-node">
    <!-- Directory node -->
    <div
      v-if="node.isDirectory"
      class="artifacts-tree-node__directory"
      :style="indentStyle"
      @click="toggleExpand"
    >
      <ChevronDown v-if="isExpanded" class="artifacts-tree-node__chevron" :size="16" />
      <ChevronRight v-else class="artifacts-tree-node__chevron" :size="16" />
      <span class="artifacts-tree-node__name">{{ node.name }}</span>
    </div>

    <!-- File node -->
    <div
      v-else
      class="artifacts-tree-node__file"
      :style="indentStyle"
      @click="handleFileClick"
    >
      <span
        class="artifacts-tree-node__status-dot"
        :style="{ backgroundColor: statusColor }"
      />
      <span class="artifacts-tree-node__name">{{ node.name }}</span>
      <span v-if="statText" class="artifacts-tree-node__stats">{{ statText }}</span>
    </div>

    <!-- Recursive children -->
    <template v-if="node.isDirectory && isExpanded">
      <ArtifactsTreeNode
        v-for="child in node.children"
        :key="child.fullPath"
        :node="child"
        :depth="depth + 1"
        @select-file="handleChildSelectFile"
      />
    </template>
  </div>
</template>

<style scoped>
.artifacts-tree-node {
  /* Container for node and children */
}

.artifacts-tree-node__directory,
.artifacts-tree-node__file {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 4px 8px;
  cursor: pointer;
  transition: background-color var(--transition);
  border-radius: 4px;
}

.artifacts-tree-node__directory:hover,
.artifacts-tree-node__file:hover {
  background-color: var(--bg);
}

.artifacts-tree-node__chevron {
  flex-shrink: 0;
  color: var(--muted);
  transition: transform var(--transition);
}

.artifacts-tree-node__status-dot {
  width: 8px;
  height: 8px;
  border-radius: 50%;
  flex-shrink: 0;
}

.artifacts-tree-node__name {
  color: var(--text);
  font-size: 14px;
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.artifacts-tree-node__stats {
  color: var(--muted);
  font-size: 12px;
  flex-shrink: 0;
  margin-left: auto;
}
</style>
