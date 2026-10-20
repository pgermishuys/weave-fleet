<script setup lang="ts">
import { computed } from "vue";
import { rememberFoldClick, sharedMarkdownRenderer, type FoldEnv } from "@/lib/markdown-renderer";

/** The agent's words as Markdown, with the desktop's renderer (raw HTML off) and its .md-content styles. */
const props = defineProps<{ text: string }>();
const renderer = sharedMarkdownRenderer();
// What the reader opened or closed, by position, so a redraw keeps it.
const folds = new Map<string, boolean>();
const html = computed(() => renderer.render(props.text, { folds } satisfies FoldEnv));
</script>

<template>
  <!-- eslint-disable vue/no-v-html -- Markdown rendered with raw HTML off, as in MessageBubble. -->
  <div
    class="ph-amsg md-content"
    @click="rememberFoldClick($event, folds)"
    v-html="html"
  />
  <!-- eslint-enable vue/no-v-html -->
</template>
