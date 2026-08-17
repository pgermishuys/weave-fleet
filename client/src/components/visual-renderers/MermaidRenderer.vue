<script setup lang="ts">
import { ref, onMounted, onUnmounted, watch, nextTick } from 'vue'
import mermaid from 'mermaid'
import svgPanZoom from 'svg-pan-zoom'
import type SvgPanZoom from 'svg-pan-zoom'
import { sanitizeHtml } from '@/lib/sanitize-html'

const props = defineProps<{
  content: string
}>()

const MAX_SIZE = 50 * 1024 // 50KB
const renderedSvg = ref<string>('')
const errorMessage = ref<string>('')
const showFallback = ref(false)
const containerRef = ref<HTMLElement | null>(null)

let mermaidInitialized = false
let renderCounter = 0
let panZoomInstance: SvgPanZoom.Instance | null = null

function initializeMermaid() {
  if (!mermaidInitialized) {
    mermaid.initialize({
      startOnLoad: false,
      securityLevel: 'strict',
      htmlLabels: false,
      theme: 'default'
    })
    mermaidInitialized = true
  }
}

function cleanupPanZoom() {
  if (panZoomInstance) {
    panZoomInstance.destroy()
    panZoomInstance = null
  }
}

function initializePanZoom() {
  cleanupPanZoom()
  
  if (!containerRef.value) return
  
  const svgElement = containerRef.value.querySelector('svg')
  if (!svgElement) return
  
  try {
    panZoomInstance = svgPanZoom(svgElement, {
      zoomEnabled: true,
      controlIconsEnabled: false,
      fit: true,
      center: true,
      minZoom: 0.5,
      maxZoom: 5,
      mouseWheelZoomEnabled: true,
      dblClickZoomEnabled: false,
      preventMouseEventsDefault: true
    })
  } catch (error) {
    console.error('Failed to initialize svg-pan-zoom:', error)
  }
}

function handleZoomIn() {
  panZoomInstance?.zoomIn()
}

function handleZoomOut() {
  panZoomInstance?.zoomOut()
}

function handleFit() {
  if (panZoomInstance) {
    panZoomInstance.fit()
    panZoomInstance.center()
  }
}

async function renderMermaid(content: string) {
  // Clean up existing pan-zoom instance
  cleanupPanZoom()
  
  // Reset state
  renderedSvg.value = ''
  errorMessage.value = ''
  showFallback.value = false

  // Validate size
  const byteLength = new TextEncoder().encode(content).length
  if (byteLength > MAX_SIZE) {
    showFallback.value = true
    errorMessage.value = `Content exceeds 50KB limit (${Math.round(byteLength / 1024)}KB)`
    return
  }

  // Validate content is not empty
  if (!content.trim()) {
    errorMessage.value = 'Empty content'
    showFallback.value = true
    return
  }

  try {
    initializeMermaid()
    
    // Generate unique ID for this render
    const id = `mermaid-${Date.now()}-${++renderCounter}`
    
    // Render the diagram
    const { svg } = await mermaid.render(id, content)
    
    // Sanitize the SVG output
    const sanitized = sanitizeHtml(svg)
    
    renderedSvg.value = sanitized
    
    // Initialize pan-zoom after DOM update
    await nextTick()
    initializePanZoom()
  } catch (error) {
    console.error('Mermaid render error:', error)
    errorMessage.value = error instanceof Error ? error.message : 'Failed to render diagram'
    showFallback.value = true
  }
}

onMounted(() => {
  renderMermaid(props.content)
})

onUnmounted(() => {
  cleanupPanZoom()
})

watch(() => props.content, (newContent) => {
  renderMermaid(newContent)
})
</script>

<template>
  <div class="mermaid-renderer">
    <div v-if="renderedSvg" class="mermaid-container">
      <div ref="containerRef" class="mermaid-output" v-html="renderedSvg"></div>
      <div class="zoom-controls">
        <button @click="handleZoomIn" class="zoom-btn" title="Zoom in">+</button>
        <button @click="handleZoomOut" class="zoom-btn" title="Zoom out">−</button>
        <button @click="handleFit" class="zoom-btn" title="Fit to view">⊡</button>
      </div>
    </div>
    <div v-else-if="showFallback" class="mermaid-fallback">
      <div v-if="errorMessage" class="error-message">
        {{ errorMessage }}
      </div>
      <pre class="raw-content">{{ content }}</pre>
    </div>
  </div>
</template>

<style scoped>
.mermaid-renderer {
  width: 100%;
  height: 100%;
  display: flex;
  flex-direction: column;
  min-height: 0;
}

.mermaid-container {
  position: relative;
  width: 100%;
  flex: 1;
  min-height: 0;
  background-color: #ffffff;
  border-radius: 0.375rem;
}

.mermaid-output {
  display: flex;
  justify-content: center;
  align-items: center;
  width: 100%;
  height: 100%;
  padding: 1rem;
  cursor: grab;
}

.mermaid-output:active {
  cursor: grabbing;
}

.mermaid-output :deep(svg) {
  width: 100%;
  height: 100%;
}

.zoom-controls {
  position: absolute;
  bottom: 1rem;
  right: 1rem;
  display: flex;
  gap: 0.25rem;
  background: rgba(0, 0, 0, 0.7);
  border-radius: 0.375rem;
  padding: 0.25rem;
  backdrop-filter: blur(4px);
}

.zoom-btn {
  width: 2rem;
  height: 2rem;
  border: none;
  background: transparent;
  color: white;
  font-size: 1.125rem;
  font-weight: 600;
  cursor: pointer;
  border-radius: 0.25rem;
  display: flex;
  align-items: center;
  justify-content: center;
  transition: background-color 0.15s ease;
}

.zoom-btn:hover {
  background: rgba(255, 255, 255, 0.15);
}

.zoom-btn:active {
  background: rgba(255, 255, 255, 0.25);
}

.mermaid-fallback {
  padding: 1rem;
}

.error-message {
  color: #dc2626;
  background-color: #fef2f2;
  border: 1px solid #fecaca;
  border-radius: 0.375rem;
  padding: 0.75rem;
  margin-bottom: 1rem;
  font-size: 0.875rem;
}

.raw-content {
  background-color: #f9fafb;
  border: 1px solid #e5e7eb;
  border-radius: 0.375rem;
  padding: 1rem;
  overflow-x: auto;
  font-family: ui-monospace, SFMono-Regular, 'SF Mono', Menlo, Consolas, 'Liberation Mono', monospace;
  font-size: 0.875rem;
  line-height: 1.5;
  white-space: pre-wrap;
  word-break: break-word;
}
</style>
