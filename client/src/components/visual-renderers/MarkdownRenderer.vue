<script setup lang="ts">
import { computed } from 'vue'
import { createMarkdownRenderer } from '@/lib/markdown-renderer'
import { sanitizeHtml } from '@/lib/sanitize-html'

interface Props {
  content: string
}

const props = defineProps<Props>()

const md = createMarkdownRenderer()

const renderedHtml = computed(() => {
  const rawHtml = md.render(props.content)
  return sanitizeHtml(rawHtml)
})
</script>

<template>
  <div class="markdown-renderer" v-html="renderedHtml" />
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
</style>
