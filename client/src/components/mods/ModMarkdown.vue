<script setup lang="ts">
import { computed } from "vue";
import { flag, str, type ModLeafNode } from "@/components/mods/mod-style";
import { modMarkdownRenderer } from "@/components/mods/mod-markdown";

/** The conversation's Markdown look, from a renderer that draws no images and no dead links (see `mod-markdown.ts`). */
const props = defineProps<{ node: ModLeafNode }>();

const html = computed(() => modMarkdownRenderer().render(str(props.node.props.text) ?? "", {}));
</script>

<template>
  <!-- eslint-disable vue/no-v-html -->
  <div
    class="mod-markdown md-content"
    :class="{ 'mod-markdown--dim': flag(node.props.dimColor) }"
    v-html="html"
  />
  <!-- eslint-enable vue/no-v-html -->
</template>

<style scoped>
.mod-markdown {
  min-width: 0;
  max-width: 100%;
  overflow-wrap: anywhere;
}

.mod-markdown--dim {
  color: var(--muted);
}
</style>
