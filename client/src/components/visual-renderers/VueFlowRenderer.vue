<script setup lang="ts">
import { ref, computed, nextTick } from 'vue'
import { VueFlow, useVueFlow } from '@vue-flow/core'
import dagre from '@dagrejs/dagre'
import '@vue-flow/core/dist/style.css'
import '@vue-flow/core/dist/theme-default.css'
import { toPng } from 'html-to-image'

interface SimplifiedNode {
  id: string
  label: string
  type?: string
  group?: string
}

interface VueFlowNode {
  id: string
  type: string
  data: { label: string }
  position: { x: number; y: number }
}

interface Edge {
  id: string
  source: string
  target: string
  label?: string
  animated?: boolean
}

interface FlowContent {
  nodes: SimplifiedNode[] | VueFlowNode[]
  edges: Edge[]
  direction?: 'TB' | 'LR' | 'BT' | 'RL'
}

interface Props {
  content: string | Record<string, unknown>
  title?: string
}

const props = defineProps<Props>()

const showSource = ref(false)
const containerRef = ref<HTMLElement | null>(null)
const isExporting = ref(false)

const { fitView, getNodes } = useVueFlow()

function toKebabCase(text: string): string {
  return text
    .trim()
    .toLowerCase()
    .replace(/[^\w\s-]/g, '')
    .replace(/\s+/g, '-')
    .replace(/-+/g, '-')
    .replace(/^-+|-+$/g, '')
}

function generateFilename(title?: string): string {
  if (title && title.trim()) {
    const slug = toKebabCase(title)
    if (slug) {
      return `diagram-${slug}.png`
    }
  }
  const timestamp = new Date().toISOString().replace(/[:.]/g, '-')
  return `diagram-${timestamp}.png`
}

function triggerDownload(dataUrl: string, filename: string): void {
  const link = document.createElement('a')
  link.download = filename
  link.href = dataUrl
  link.click()
}

async function exportPng(): Promise<void> {
  const container = containerRef.value
  if (!container) return

  isExporting.value = true

  try {
    const nodes = getNodes.value
    if (nodes.length === 0) return

    const padding = 40
    const maxY = Math.max(...nodes.map(n => n.position.y + (n.dimensions?.height || 36)))
    const originalHeight = container.style.height
    container.style.height = `${maxY + padding * 2}px`

    await fitView({ padding: 0.1 })
    await nextTick()

    const edgePaths = container.querySelectorAll('.vue-flow__edge-path')
    edgePaths.forEach((path) => {
      const el = path as SVGElement
      el.style.fill = 'none'
      el.style.stroke = '#b1b1b7'
      el.style.strokeWidth = '1'
    })

    const handles = container.querySelectorAll('.vue-flow__handle')
    handles.forEach((handle) => {
      (handle as HTMLElement).style.display = 'none'
    })

    const interactionPaths = container.querySelectorAll('.vue-flow__edge-interaction')
    interactionPaths.forEach((path) => {
      (path as SVGElement).style.display = 'none'
    })

    const edgeTexts = container.querySelectorAll('.vue-flow__edge-text')
    edgeTexts.forEach((text) => {
      const el = text as SVGElement
      el.style.fontSize = '10px'
    })

    const edgeTextBgs = container.querySelectorAll('.vue-flow__edge-textbg')
    edgeTextBgs.forEach((bg) => {
      const el = bg as SVGElement
      el.style.fill = 'white'
    })

    const dataUrl = await toPng(container, { pixelRatio: 2 })
    const filename = generateFilename(props.title)
    triggerDownload(dataUrl, filename)

    edgePaths.forEach((path) => {
      const el = path as SVGElement
      el.style.fill = ''
      el.style.stroke = ''
      el.style.strokeWidth = ''
    })
    handles.forEach((handle) => {
      (handle as HTMLElement).style.display = ''
    })
    interactionPaths.forEach((path) => {
      (path as SVGElement).style.display = ''
    })
    edgeTexts.forEach((text) => {
      const el = text as SVGElement
      el.style.fontSize = ''
    })
    edgeTextBgs.forEach((bg) => {
      const el = bg as SVGElement
      el.style.fill = ''
    })

    container.style.height = originalHeight
    await fitView({ padding: 0.1 })
  } catch (error) {
    console.error('Failed to export PNG:', error)
  } finally {
    isExporting.value = false
  }
}

const parsedContent = computed<FlowContent>(() => {
  if (typeof props.content === 'string') {
    try {
      return JSON.parse(props.content) as FlowContent
    } catch {
      return { nodes: [], edges: [], direction: 'TB' }
    }
  }
  return props.content as unknown as FlowContent
})

const normalizedNodes = computed<VueFlowNode[]>(() => {
  const nodes = parsedContent.value.nodes || []
  return nodes.map((node) => {
    if ('position' in node) {
      return node as VueFlowNode
    }
    const simplified = node as SimplifiedNode
    return {
      id: simplified.id,
      type: simplified.type || 'default',
      data: { label: simplified.label },
      position: { x: 0, y: 0 },
    }
  })
})

const normalizedEdges = computed<Edge[]>(() => {
  return parsedContent.value.edges || []
})

function layoutNodes(nodes: VueFlowNode[], edges: Edge[], direction = 'TB'): VueFlowNode[] {
  const g = new dagre.graphlib.Graph()
  g.setDefaultEdgeLabel(() => ({}))
  g.setGraph({ rankdir: direction, nodesep: 50, ranksep: 80 })
  
  nodes.forEach(node => {
    g.setNode(node.id, { width: 172, height: 36 })
  })
  edges.forEach(edge => {
    g.setEdge(edge.source, edge.target)
  })
  
  dagre.layout(g)
  
  return nodes.map(node => {
    const pos = g.node(node.id)
    return { ...node, position: { x: pos.x - 86, y: pos.y - 18 } }
  })
}

const layoutedNodes = computed<VueFlowNode[]>(() => {
  const direction = parsedContent.value.direction || 'TB'
  return layoutNodes(normalizedNodes.value, normalizedEdges.value, direction)
})
</script>

<template>
  <div class="flow-renderer">
    <div v-if="title" class="flow-title">{{ title }}</div>
    <div class="flow-toolbar">
      <button
        class="toolbar-btn"
        :class="{ active: showSource }"
        @click="showSource = !showSource"
        :aria-label="showSource ? 'Show diagram' : 'Show source'"
      >
        <svg v-if="!showSource" width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
          <polyline points="16 18 22 12 16 6"></polyline>
          <polyline points="8 6 2 12 8 18"></polyline>
        </svg>
        <svg v-else width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
          <rect x="3" y="3" width="18" height="18" rx="2" ry="2"></rect>
          <circle cx="8.5" cy="8.5" r="1.5"></circle>
          <polyline points="21 15 16 10 5 21"></polyline>
        </svg>
      </button>
      <button
        class="toolbar-btn"
        @click="exportPng"
        aria-label="Download PNG"
        :disabled="showSource || isExporting"
      >
        <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
          <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"></path>
          <polyline points="7 10 12 15 17 10"></polyline>
          <line x1="12" y1="15" x2="12" y2="3"></line>
        </svg>
      </button>
    </div>

    <!-- Source view -->
    <pre v-if="showSource" class="flow-source"><code>{{ JSON.stringify(parsedContent, null, 2) }}</code></pre>

    <!-- Diagram view -->
    <div v-else ref="containerRef" class="flow-container">
      <VueFlow
        :nodes="layoutedNodes"
        :edges="normalizedEdges"
        fit-view-on-init
      />
    </div>
  </div>
</template>

<style scoped>
.flow-renderer {
  width: 100%;
  height: 100%;
  display: flex;
  flex-direction: column;
  background-color: #ffffff;
  border: 1px solid #e5e7eb;
  border-radius: 0.375rem;
  overflow: hidden;
}

.flow-title {
  padding: 0.75rem 1rem;
  font-weight: 600;
  font-size: 0.875rem;
  border-bottom: 1px solid #e5e7eb;
  background-color: #f9fafb;
}

.flow-toolbar {
  display: flex;
  gap: 0.5rem;
  padding: 0.5rem;
  border-bottom: 1px solid #e5e7eb;
  background-color: #f9fafb;
}

.toolbar-btn {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 2rem;
  height: 2rem;
  padding: 0;
  background-color: #ffffff;
  border: 1px solid #e5e7eb;
  border-radius: 0.25rem;
  cursor: pointer;
  color: #6b7280;
  transition: all 0.15s ease;
}

.toolbar-btn:hover:not(:disabled) {
  background-color: #f3f4f6;
  border-color: #d1d5db;
  color: #374151;
}

.toolbar-btn:active:not(:disabled) {
  background-color: #e5e7eb;
}

.toolbar-btn.active {
  background-color: #3b82f6;
  border-color: #3b82f6;
  color: #ffffff;
}

.toolbar-btn:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.flow-source {
  flex: 1;
  margin: 0;
  padding: 1rem;
  background-color: #f9fafb;
  border: none;
  overflow: auto;
  font-family: ui-monospace, SFMono-Regular, 'SF Mono', Menlo, Consolas, 'Liberation Mono', monospace;
  font-size: 0.875rem;
  line-height: 1.5;
  white-space: pre-wrap;
  word-break: break-word;
}

.flow-source code {
  font-family: inherit;
}

.flow-container {
  position: relative;
  width: 100%;
  height: 400px;
  background-color: #ffffff;
}

.flow-container :deep(.vue-flow) {
  width: 100%;
  height: 100%;
}

.flow-container :deep(.vue-flow__node) {
  background-color: #ffffff;
  border: 1px solid #e5e7eb;
  border-radius: 0.375rem;
  padding: 0.5rem 1rem;
  font-size: 0.875rem;
  color: #374151;
}

.flow-container :deep(.vue-flow__node:hover) {
  border-color: #3b82f6;
  box-shadow: 0 1px 3px 0 rgba(0, 0, 0, 0.1);
}

.flow-container :deep(.vue-flow__edge-path) {
  stroke: #9ca3af;
  stroke-width: 1.5;
}

.flow-container :deep(.vue-flow__edge-text) {
  font-size: 0.75rem;
  fill: #6b7280;
}

.flow-container :deep(.vue-flow__edge-textbg) {
  fill: #ffffff;
}

.flow-container :deep(.vue-flow__handle) {
  width: 8px;
  height: 8px;
  background-color: #3b82f6;
  border: 2px solid #ffffff;
}
</style>
