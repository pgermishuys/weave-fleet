<script setup lang="ts">
import { ref, computed, nextTick, watch, h, type FunctionalComponent } from 'vue'
import { VueFlow, useVueFlow, MarkerType, Position, type EdgeMarker, type Edge as VueFlowEdge } from '@vue-flow/core'
import dagre from '@dagrejs/dagre'
import '@vue-flow/core/dist/style.css'
import '@vue-flow/core/dist/theme-default.css'
import { toPng } from 'html-to-image'

interface SimplifiedNode {
  id: string
  label: string
  type?: string
  group?: string
  /** A second, quieter line under the label. */
  detail?: string
  /** Top-left position, kept when both are set; otherwise the node is laid out. */
  x?: number
  y?: number
}

interface VueFlowNode {
  id: string
  type: string
  data: { label: string | FunctionalComponent }
  position: { x: number; y: number }
  sourcePosition?: Position
  targetPosition?: Position
}

interface Edge {
  id: string
  source: string
  target: string
  label?: string
  animated?: boolean
  /** "dashed" or "planned" draw the edge that way; any other value passes through to Vue Flow. */
  style?: unknown
  class?: string
  markerEnd?: EdgeMarker | string
}

interface FlowContent {
  nodes: SimplifiedNode[] | VueFlowNode[]
  edges: Edge[]
  direction?: 'TB' | 'LR' | 'BT' | 'RL'
}

interface Props {
  content: string | Record<string, unknown>
  title?: string
  /** Pan and zoom only: boxes can't be dragged, selected or connected. */
  readonly?: boolean
}

const props = defineProps<Props>()

const showSource = ref(false)
const containerRef = ref<HTMLElement | null>(null)
const isExporting = ref(false)

const { fitView, getNodes, onNodesInitialized } = useVueFlow()

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

const NODE_WIDTH = 172
const NODE_HEIGHT = 36
const NODE_WITH_DETAIL_HEIGHT = 54

const HANDLE_POSITIONS: Record<string, { source: Position; target: Position }> = {
  TB: { source: Position.Bottom, target: Position.Top },
  BT: { source: Position.Top, target: Position.Bottom },
  LR: { source: Position.Right, target: Position.Left },
  RL: { source: Position.Left, target: Position.Right },
}

const EDGE_ARROW: EdgeMarker = { type: MarkerType.ArrowClosed, color: 'var(--flow-edge)' }
const PLANNED_EDGE_ARROW: EdgeMarker = { type: MarkerType.ArrowClosed, color: 'var(--accent)' }

// Node ids with a position of their own, which the layout leaves alone.
const fixedNodeIds = computed(() => new Set(
  (parsedContent.value.nodes || []).flatMap((node) => {
    const simplified = node as SimplifiedNode
    return !('position' in node) && Number.isFinite(simplified.x) && Number.isFinite(simplified.y) ? [node.id] : []
  }),
))

function labelWithDetail(label: string, detail: string): FunctionalComponent {
  return () => [
    h('span', { class: 'flow-node__label' }, label),
    h('span', { class: 'flow-node__detail' }, detail),
  ]
}

const normalizedNodes = computed<VueFlowNode[]>(() => {
  const nodes = parsedContent.value.nodes || []
  const handles = HANDLE_POSITIONS[parsedContent.value.direction || 'TB'] ?? HANDLE_POSITIONS.TB
  return nodes.map((node) => {
    if ('position' in node) {
      return node as VueFlowNode
    }
    const simplified = node as SimplifiedNode
    return {
      id: simplified.id,
      type: simplified.type || 'default',
      data: { label: simplified.detail ? labelWithDetail(simplified.label, simplified.detail) : simplified.label },
      position: fixedNodeIds.value.has(simplified.id) ? { x: simplified.x!, y: simplified.y! } : { x: 0, y: 0 },
      sourcePosition: handles.source,
      targetPosition: handles.target,
    }
  })
})

const normalizedEdges = computed<VueFlowEdge[]>(() => {
  return (parsedContent.value.edges || []).map((edge) => {
    const drawn = edge.style === 'dashed' || edge.style === 'planned' ? edge.style : null
    const { style, ...rest } = edge
    return {
      markerEnd: drawn === 'planned' ? PLANNED_EDGE_ARROW : EDGE_ARROW,
      ...rest,
      ...(drawn
        ? { class: [edge.class, `flow-edge--${drawn}`].filter(Boolean).join(' ') }
        : { style: style as VueFlowEdge['style'] }),
    }
  })
})

function nodeHeight(node: VueFlowNode): number {
  return typeof node.data.label === 'string' ? NODE_HEIGHT : NODE_WITH_DETAIL_HEIGHT
}

function layoutNodes(nodes: VueFlowNode[], edges: Array<Pick<Edge, 'source' | 'target'>>, direction = 'TB'): VueFlowNode[] {
  const g = new dagre.graphlib.Graph()
  g.setDefaultEdgeLabel(() => ({}))
  g.setGraph({ rankdir: direction, nodesep: 50, ranksep: 80 })
  
  nodes.forEach(node => {
    g.setNode(node.id, { width: NODE_WIDTH, height: nodeHeight(node) })
  })
  edges.forEach(edge => {
    g.setEdge(edge.source, edge.target)
  })
  
  dagre.layout(g)
  
  return nodes.map(node => {
    if (fixedNodeIds.value.has(node.id)) return node
    const pos = g.node(node.id)
    return { ...node, position: { x: pos.x - NODE_WIDTH / 2, y: pos.y - nodeHeight(node) / 2 } }
  })
}

const layoutedNodes = computed<VueFlowNode[]>(() => {
  const direction = parsedContent.value.direction || 'TB'
  return layoutNodes(normalizedNodes.value, normalizedEdges.value, direction)
})

// A diagram that changes in place (an agent revising a canvas) is fitted
// again once its new boxes have been measured.
let refitPending = false
watch(layoutedNodes, async () => {
  await nextTick()
  const nodes = getNodes.value
  if (nodes.length > 0 && nodes.every((node) => node.dimensions.width > 0)) {
    void fitView({ padding: 0.1, duration: 200 })
  } else {
    refitPending = true
  }
})
onNodesInitialized(() => {
  if (!refitPending) return
  refitPending = false
  void fitView({ padding: 0.1, duration: 200 })
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
        :class="{ 'flow-readonly': readonly }"
        :nodes-draggable="!readonly"
        :nodes-connectable="!readonly"
        :elements-selectable="!readonly"
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
  background-color: transparent;
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  overflow: hidden;
}

.flow-title {
  padding: 0.75rem 1rem;
  font-weight: 600;
  font-size: 13px;
  color: var(--text);
  border-bottom: 1px solid var(--border);
}

.flow-toolbar {
  display: flex;
  gap: 4px;
  padding: 6px 8px;
  border-bottom: 1px solid var(--border);
}

.toolbar-btn {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 28px;
  height: 28px;
  padding: 0;
  background-color: transparent;
  border: 1px solid transparent;
  border-radius: var(--radius-btn);
  cursor: pointer;
  color: var(--muted);
  transition: background-color var(--transition), color var(--transition);
}

.toolbar-btn:hover:not(:disabled) {
  background-color: color-mix(in srgb, var(--text) 6%, transparent);
  color: var(--text);
}

.toolbar-btn:active:not(:disabled) {
  background-color: color-mix(in srgb, var(--text) 10%, transparent);
}

.toolbar-btn.active {
  background-color: var(--accent-dim);
  color: var(--accent);
}

.toolbar-btn:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.flow-source {
  flex: 1;
  margin: 0;
  padding: 1rem;
  background-color: color-mix(in srgb, var(--text) 4%, transparent);
  color: var(--text);
  border: none;
  overflow: auto;
  font-family: var(--font-mono-stack);
  font-size: 12px;
  line-height: 1.5;
  white-space: pre-wrap;
  word-break: break-word;
}

.flow-source code {
  font-family: inherit;
}

.flow-container {
  --flow-edge: color-mix(in srgb, var(--text) 38%, transparent);
  position: relative;
  width: 100%;
  flex: 1;
  min-height: 400px;
  background-color: transparent;
}

.flow-container :deep(.vue-flow) {
  width: 100%;
  height: 100%;
}

.flow-container :deep(.vue-flow__node) {
  background-color: var(--card-bg);
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  padding: 0.5rem 1rem;
  font-size: 12.5px;
  font-weight: 500;
  color: var(--text);
}

.flow-container :deep(.vue-flow__node:hover) {
  border-color: color-mix(in srgb, var(--text) 28%, transparent);
}

.flow-container :deep(.vue-flow__node.selected) {
  border-color: var(--accent);
  box-shadow: 0 0 0 1px var(--accent);
}

.flow-container :deep(.vue-flow__node-default:has(.flow-node__detail)) {
  display: flex;
  flex-direction: column;
  gap: 2px;
  text-align: left;
}

.flow-container :deep(.flow-node__detail) {
  overflow: hidden;
  font-family: var(--font-mono-stack);
  font-size: 10.5px;
  font-weight: 400;
  color: var(--muted);
  text-overflow: ellipsis;
  white-space: nowrap;
}

.flow-container :deep(.vue-flow__edge-path) {
  stroke: var(--flow-edge);
  stroke-width: 1.4;
}

.flow-container :deep(.flow-edge--dashed .vue-flow__edge-path) {
  stroke-dasharray: 5 4;
}

.flow-container :deep(.flow-edge--planned .vue-flow__edge-path) {
  stroke: var(--accent);
  stroke-dasharray: 5 4;
}

.flow-container :deep(.flow-edge--planned .vue-flow__edge-text) {
  fill: var(--accent);
  font-weight: 500;
}

.flow-container :deep(.flow-readonly .vue-flow__handle) {
  visibility: hidden;
}

.flow-container :deep(.flow-readonly .vue-flow__node) {
  cursor: default;
}

.flow-container :deep(.vue-flow__edge-text) {
  font-size: 11px;
  fill: var(--muted);
}

.flow-container :deep(.vue-flow__edge-textbg) {
  fill: var(--panel-bg);
}

.flow-container :deep(.vue-flow__handle) {
  width: 6px;
  height: 6px;
  background-color: var(--accent);
  border: 1.5px solid var(--card-bg);
}

@media (prefers-reduced-motion: reduce) {
  .toolbar-btn {
    transition: none;
  }
}
</style>
