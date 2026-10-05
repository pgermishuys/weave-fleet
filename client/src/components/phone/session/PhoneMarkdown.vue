<script setup lang="ts">
import { computed } from "vue";
import { sharedMarkdownRenderer } from "@/lib/markdown-renderer";

/** The agent's words as Markdown, with the desktop's renderer (raw HTML off). */
const props = defineProps<{ text: string }>();
const renderer = sharedMarkdownRenderer();
const html = computed(() => renderer.render(props.text));
</script>

<template>
  <!-- eslint-disable-next-line vue/no-v-html -->
  <div
    class="pmd prose-chat"
    v-html="html"
  />
</template>

<style scoped>
.pmd {
  font-size: 15px;
  line-height: 1.55;
  overflow-wrap: anywhere;
}

.pmd :deep(p) {
  margin: 0 0 8px;
}

.pmd :deep(p:last-child) {
  margin-bottom: 0;
}

.pmd :deep(pre) {
  overflow-x: auto;
  padding: 8px 10px;
  border-radius: var(--radius-btn);
  background: var(--main-bg);
  font-size: 12px;
}

.pmd :deep(code) {
  font-family: var(--font-mono-stack);
  font-size: 0.9em;
}

.pmd :deep(ul),
.pmd :deep(ol) {
  margin: 0 0 8px;
  padding-left: 20px;
}
</style>
