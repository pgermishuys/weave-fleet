<script setup lang="ts">
import { ref, onMounted, watch } from 'vue'
import mermaid from 'mermaid'
import { sanitizeHtml } from '@/lib/sanitize-html'

const props = defineProps<{
  content: string
}>()

const MAX_SIZE = 50 * 1024 // 50KB
const renderedSvg = ref<string>('')
const errorMessage = ref<string>('')
const showFallback = ref(false)

let mermaidInitialized = false
let renderCounter = 0

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

async function renderMermaid(content: string) {
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
  } catch (error) {
    console.error('Mermaid render error:', error)
    errorMessage.value = error instanceof Error ? error.message : 'Failed to render diagram'
    showFallback.value = true
  }
}

onMounted(() => {
  renderMermaid(props.content)
})

watch(() => props.content, (newContent) => {
  renderMermaid(newContent)
})
</script>

<template>
  <div class="mermaid-renderer">
    <div v-if="renderedSvg" class="mermaid-output" v-html="renderedSvg"></div>
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
  overflow: auto;
}

.mermaid-output {
  display: flex;
  justify-content: center;
  padding: 1rem;
}

.mermaid-output :deep(svg) {
  max-width: 100%;
  height: auto;
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
