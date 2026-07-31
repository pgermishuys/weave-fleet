<script setup lang="ts">
import { computed, ref, onMounted, onUnmounted } from 'vue'
import { createMarkdownRenderer } from '@/lib/markdown-renderer'
import { sanitizeHtml } from '@/lib/sanitize-html'
import type { ElementAnchor, TextRangeAnchor, AnnotationAnchor } from '@/lib/annotation-types'

interface Props {
  content: string
  annotatable?: boolean
}

const props = withDefaults(defineProps<Props>(), {
  annotatable: false
})

const emit = defineEmits<{
  annotate: [anchor: AnnotationAnchor, position: { x: number; y: number }]
}>()

const md = createMarkdownRenderer()
const containerRef = ref<HTMLElement | null>(null)

const renderedHtml = computed(() => {
  const rawHtml = md.render(props.content)
  return sanitizeHtml(rawHtml)
})

// Block elements that can be annotated
const BLOCK_SELECTOR = 'h1,h2,h3,h4,h5,h6,p,li,pre,blockquote,table'

// Track currently highlighted element
let currentHighlight: HTMLElement | null = null

function handleMouseEnter(event: Event) {
  const target = event.target as HTMLElement
  if (target.matches(BLOCK_SELECTOR)) {
    target.classList.add('annotation-highlight')
    currentHighlight = target
  }
}

function handleMouseLeave(event: Event) {
  const target = event.target as HTMLElement
  if (target.matches(BLOCK_SELECTOR)) {
    target.classList.remove('annotation-highlight')
    if (currentHighlight === target) {
      currentHighlight = null
    }
  }
}

function buildSelectorPath(element: HTMLElement, container: HTMLElement): string {
  const path: string[] = []
  let current: HTMLElement | null = element

  while (current && current !== container) {
    const parent: HTMLElement | null = current.parentElement
    if (!parent) break

    const siblings = Array.from(parent.children).filter(
      (child): child is HTMLElement => child instanceof HTMLElement && child.tagName === current!.tagName
    )
    const index = siblings.indexOf(current) + 1
    path.unshift(`${current.tagName.toLowerCase()}:nth-of-type(${index})`)
    current = parent
  }

  return path.join(' > ')
}

function getTextOffset(container: HTMLElement, node: Node, offset: number): number {
  const range = document.createRange()
  range.setStart(container, 0)
  range.setEnd(node, offset)
  return range.toString().length
}

function handleMouseUp(event: MouseEvent) {
  if (!containerRef.value) return

  const selection = window.getSelection()
  if (!selection || selection.isCollapsed) return

  const range = selection.getRangeAt(0)
  const selectedText = range.toString().trim()

  if (!selectedText) return

  // Check if selection is within our container
  if (!containerRef.value.contains(range.commonAncestorContainer)) return

  // Build TextRangeAnchor
  const startOffset = getTextOffset(containerRef.value, range.startContainer, range.startOffset)
  const endOffset = getTextOffset(containerRef.value, range.endContainer, range.endOffset)

  const anchor: TextRangeAnchor = {
    type: 'text-range',
    selectedText,
    startOffset,
    endOffset
  }

  emit('annotate', anchor, { x: event.clientX, y: event.clientY })

  // Clear selection
  selection.removeAllRanges()
}

function handleClick(event: MouseEvent) {
  if (!containerRef.value) return

  // Check if there's a text selection
  const selection = window.getSelection()
  if (selection && !selection.isCollapsed) return

  // Find the closest block element
  const target = event.target as HTMLElement
  const blockElement = target.closest(BLOCK_SELECTOR) as HTMLElement | null

  if (!blockElement || !containerRef.value.contains(blockElement)) return

  // Build ElementAnchor
  const selectorPath = buildSelectorPath(blockElement, containerRef.value)
  const elementText = blockElement.textContent?.trim() || ''

  const anchor: ElementAnchor = {
    type: 'element',
    selectorPath,
    elementText
  }

  emit('annotate', anchor, { x: event.clientX, y: event.clientY })
}

onMounted(() => {
  if (!props.annotatable || !containerRef.value) return

  const container = containerRef.value

  // Use event delegation for mouseenter/mouseleave
  container.addEventListener('mouseenter', handleMouseEnter, true)
  container.addEventListener('mouseleave', handleMouseLeave, true)
  container.addEventListener('mouseup', handleMouseUp)
  container.addEventListener('click', handleClick)
})

onUnmounted(() => {
  if (!containerRef.value) return

  const container = containerRef.value

  container.removeEventListener('mouseenter', handleMouseEnter, true)
  container.removeEventListener('mouseleave', handleMouseLeave, true)
  container.removeEventListener('mouseup', handleMouseUp)
  container.removeEventListener('click', handleClick)
})
</script>

<template>
  <div ref="containerRef" class="markdown-renderer" v-html="renderedHtml" />
</template>

<style scoped>
.markdown-renderer {
  line-height: 1.6;
  word-wrap: break-word;
}

.markdown-renderer :deep(h1),
.markdown-renderer :deep(h2),
.markdown-renderer :deep(h3),
.markdown-renderer :deep(h4),
.markdown-renderer :deep(h5),
.markdown-renderer :deep(h6) {
  margin-top: 1.5em;
  margin-bottom: 0.5em;
  font-weight: 600;
}

.markdown-renderer :deep(h1) {
  font-size: 2em;
}

.markdown-renderer :deep(h2) {
  font-size: 1.5em;
}

.markdown-renderer :deep(h3) {
  font-size: 1.25em;
}

.markdown-renderer :deep(p) {
  margin-bottom: 1em;
}

.markdown-renderer :deep(ul),
.markdown-renderer :deep(ol) {
  margin-bottom: 1em;
  padding-left: 2em;
}

.markdown-renderer :deep(li) {
  margin-bottom: 0.25em;
}

.markdown-renderer :deep(code) {
  background-color: rgba(0, 0, 0, 0.05);
  padding: 0.2em 0.4em;
  border-radius: 3px;
  font-family: 'Courier New', Courier, monospace;
  font-size: 0.9em;
}

.markdown-renderer :deep(pre) {
  margin-bottom: 1em;
  overflow-x: auto;
}

.markdown-renderer :deep(pre code) {
  background-color: transparent;
  padding: 0;
}

.markdown-renderer :deep(blockquote) {
  border-left: 4px solid #ddd;
  padding-left: 1em;
  margin-left: 0;
  color: #666;
}

.markdown-renderer :deep(a) {
  color: #0066cc;
  text-decoration: none;
}

.markdown-renderer :deep(a:hover) {
  text-decoration: underline;
}

.markdown-renderer :deep(table) {
  border-collapse: collapse;
  width: 100%;
  margin-bottom: 1em;
}

.markdown-renderer :deep(th),
.markdown-renderer :deep(td) {
  border: 1px solid #ddd;
  padding: 0.5em;
  text-align: left;
}

.markdown-renderer :deep(th) {
  background-color: rgba(0, 0, 0, 0.05);
  font-weight: 600;
}

.markdown-renderer :deep(img) {
  max-width: 100%;
  height: auto;
}

.markdown-renderer :deep(hr) {
  border: none;
  border-top: 1px solid #ddd;
  margin: 1.5em 0;
}

/* Annotation highlight for hoverable block elements */
.markdown-renderer :deep(.annotation-highlight) {
  outline: 2px solid rgba(59, 130, 246, 0.5);
  background-color: rgba(59, 130, 246, 0.05);
  border-radius: 4px;
  cursor: pointer;
  transition: outline 0.15s ease, background-color 0.15s ease;
}
</style>
