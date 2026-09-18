<script setup lang="ts">
import { ref, onMounted, onUnmounted, watch, nextTick } from 'vue'
import svgPanZoom from 'svg-pan-zoom'
import type SvgPanZoom from 'svg-pan-zoom'
import { renderMermaidSvg } from '@/lib/mermaid'

const props = defineProps<{
  content: string
}>()

const MAX_SIZE = 50 * 1024 // 50KB
const renderedSvg = ref<string>('')
const errorMessage = ref<string>('')
const showFallback = ref(false)
const containerRef = ref<HTMLElement | null>(null)

let panZoomInstance: SvgPanZoom.Instance | null = null

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
    renderedSvg.value = await renderMermaidSvg(content)
    
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
      <div ref="containerRef" class="mermaid-output mermaid-diagram" v-html="renderedSvg"></div>
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
  background-color: transparent;
  border-radius: var(--radius-btn);
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
  bottom: 12px;
  right: 12px;
  display: flex;
  gap: 2px;
  background: var(--card-bg);
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  padding: 3px;
}

.zoom-btn {
  width: 26px;
  height: 26px;
  border: none;
  background: transparent;
  color: var(--muted);
  font-size: 15px;
  font-weight: 500;
  cursor: pointer;
  border-radius: calc(var(--radius-btn) - 2px);
  display: flex;
  align-items: center;
  justify-content: center;
  transition: background-color var(--transition), color var(--transition);
}

.zoom-btn:hover {
  background: color-mix(in srgb, var(--text) 6%, transparent);
  color: var(--text);
}

.zoom-btn:active {
  background: color-mix(in srgb, var(--text) 10%, transparent);
}

.mermaid-fallback {
  padding: 1rem;
}

.error-message {
  color: var(--error);
  background-color: color-mix(in srgb, var(--error) 10%, transparent);
  border: 1px solid color-mix(in srgb, var(--error) 30%, transparent);
  border-radius: var(--radius-btn);
  padding: 0.75rem;
  margin-bottom: 1rem;
  font-size: 13px;
}

.raw-content {
  background-color: color-mix(in srgb, var(--text) 4%, transparent);
  color: var(--text);
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  padding: 1rem;
  overflow-x: auto;
  font-family: var(--font-mono-stack);
  font-size: 12px;
  line-height: 1.5;
  white-space: pre-wrap;
  word-break: break-word;
}

@media (prefers-reduced-motion: reduce) {
  .zoom-btn {
    transition: none;
  }
}
</style>
