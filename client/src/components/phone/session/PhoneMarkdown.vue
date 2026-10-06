<script setup lang="ts">
import { computed } from "vue";
import { sharedMarkdownRenderer } from "@/lib/markdown-renderer";

/** The agent's words as Markdown, with the desktop's renderer (raw HTML off) and its .md-content styles. */
const props = defineProps<{ text: string }>();
const renderer = sharedMarkdownRenderer();
const html = computed(() => renderer.render(props.text));
</script>

<template>
  <!-- eslint-disable vue/no-v-html -- Markdown rendered with raw HTML off, as in MessageBubble. -->
  <div
    class="ph-amsg md-content"
    v-html="html"
  />
  <!-- eslint-enable vue/no-v-html -->
</template>
