<script setup lang="ts">
import { computed } from "vue";
import { sharedMarkdownRenderer } from "@/lib/markdown-renderer";

/** The agent's words as Markdown, with the desktop's renderer (raw HTML off). */
const props = defineProps<{ text: string }>();
const renderer = sharedMarkdownRenderer();
const html = computed(() => renderer.render(props.text));
</script>

<template>
  <!-- eslint-disable vue/no-v-html -- Markdown rendered with raw HTML off, as in MessageBubble. -->
  <div
    class="pmd prose-chat"
    v-html="html"
  />
  <!-- eslint-enable vue/no-v-html -->
</template>

<style scoped>
.pmd {
  font-size: var(--ph-t-body);
  line-height: 1.45;
  overflow-wrap: anywhere;
}

.pmd :deep(p) {
  margin: 0 0 10px;
}

.pmd :deep(p:last-child) {
  margin-bottom: 0;
}

.pmd :deep(pre) {
  overflow-x: auto;
  padding: 9px 12px;
  border-radius: 10px;
  background: var(--ph-code-bg);
  font-size: 0.8rem;
}

.pmd :deep(code) {
  font-family: var(--ph-mono);
  font-size: 0.85em;
}

.pmd :deep(ul),
.pmd :deep(ol) {
  margin: 0 0 10px;
  padding-left: 22px;
}
</style>
