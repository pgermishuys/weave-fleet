<script setup lang="ts">
import { computed } from "vue";
import ModNode from "@/components/mods/ModNode.vue";
import { flag, roleColor, type ModContainerNode } from "@/components/mods/mod-style";

/** Text in a role colour. It wraps (long words break), or on `truncate` stays on one line with an ellipsis. */
const props = defineProps<{ node: ModContainerNode }>();

const p = computed(() => props.node.props);
// An explicit colour wins over `dimColor`.
const color = computed(() => roleColor(p.value.color) ?? (flag(p.value.dimColor) ? roleColor("muted") : undefined));
</script>

<template>
  <span
    class="mod-text"
    :class="{
      'mod-text--bold': flag(p.bold),
      'mod-text--italic': flag(p.italic),
      'mod-text--strike': flag(p.strikethrough),
      'mod-text--code': flag(p.code),
      'mod-text--truncate': p.wrap === 'truncate',
    }"
    :style="{ color }"
  ><ModNode
    v-for="(child, index) in node.children"
    :key="index"
    :node="child"
  /></span>
</template>

<style scoped>
.mod-text {
  min-width: 0;
  overflow-wrap: anywhere;
}

.mod-text--bold { font-weight: 600; }
.mod-text--italic { font-style: italic; }
.mod-text--strike { text-decoration: line-through; }
.mod-text--code { font-family: var(--font-mono-stack); font-size: 0.92em; }

.mod-text--truncate {
  display: block;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
</style>
