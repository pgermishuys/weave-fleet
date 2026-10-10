<script setup lang="ts">
import { computed } from "vue";
import DiffView from "@/components/session/DiffView.vue";
import { str, type ModLeafNode } from "@/components/mods/mod-style";
import { parseUnifiedDiff } from "@/lib/diff-parser";
import { sharedMarkdownRenderer } from "@/lib/markdown-renderer";

/**
 * Code as the conversation draws it: a fenced block through the conversation's Markdown renderer (same highlighting),
 * or `format: "diff"` through the tool cards' diff view. The block scrolls or wraps itself; the page never scrolls sideways.
 */
const props = defineProps<{ node: ModLeafNode }>();

const source = computed(() => str(props.node.props.source) ?? "");
const isDiff = computed(() => props.node.props.format === "diff");
const wrap = computed(() => (props.node.props.wrap === "truncate" ? "truncate" : "wrap"));
const diffLines = computed(() => (isDiff.value ? parseUnifiedDiff(source.value).lines : []));

const head = computed(() => {
  const path = str(props.node.props.path);
  if (!path) return undefined;
  const line = props.node.props.startLine;
  return typeof line === "number" ? `${path}:${line}` : path;
});

const html = computed(() => {
  if (isDiff.value) return "";
  // A fence longer than any run of backticks in the source, so the source can't end it early.
  const longest = Math.max(0, ...(source.value.match(/`+/g) ?? []).map((run) => run.length));
  const fence = "`".repeat(Math.max(3, longest + 1));
  const language = (str(props.node.props.language) ?? "").replace(/[^\w+#.-]/g, "");
  return sharedMarkdownRenderer().render(`${fence}${language}\n${source.value}\n${fence}`, {});
});
</script>

<template>
  <div
    class="mod-code"
    :data-wrap="wrap"
  >
    <div
      v-if="head"
      class="mod-code__head"
    >
      {{ head }}
    </div>
    <DiffView
      v-if="isDiff"
      :lines="diffLines"
    />
    <!-- eslint-disable vue/no-v-html -->
    <div
      v-else
      class="mod-code__body md-content"
      v-html="html"
    />
    <!-- eslint-enable vue/no-v-html -->
  </div>
</template>

<style scoped>
.mod-code {
  min-width: 0;
  max-width: 100%;
  overflow-x: auto;
}

.mod-code__head {
  padding: 0 2px 4px;
  color: var(--muted);
  font-family: var(--font-mono-stack);
  font-size: 11.5px;
  overflow-wrap: anywhere;
}

.mod-code__body :deep(pre) {
  margin: 0;
}

.mod-code[data-wrap="wrap"] .mod-code__body :deep(code) {
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

.mod-code[data-wrap="truncate"] .mod-code__body :deep(code) {
  white-space: pre;
}
</style>
