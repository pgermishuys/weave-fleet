<script setup lang="ts">
import { computed } from "vue";

/** A GitHub label in its own colour, tinted so it reads on any theme. */
const props = defineProps<{
  name: string;
  color: string;
}>();

defineEmits<{
  click: [name: string];
}>();

const HEX_COLOR = /^[0-9a-fA-F]{6}$/;
const color = computed(() => `#${HEX_COLOR.test(props.color) ? props.color : "888888"}`);
</script>

<template>
  <span
    class="gh-label"
    :style="{ '--gh-label': color }"
    @click="$emit('click', name)"
  >{{ name }}</span>
</template>

<style scoped>
.gh-label {
  display: inline-flex;
  align-items: center;
  flex-shrink: 0;
  height: 18px;
  padding: 0 7px;
  border: 1px solid color-mix(in srgb, var(--gh-label) 35%, transparent);
  border-radius: 999px;
  background: color-mix(in srgb, var(--gh-label) 14%, transparent);
  /* The label's own colour, lifted toward the text colour so dark labels stay readable. */
  color: color-mix(in srgb, var(--gh-label) 70%, var(--text));
  font-size: 11px;
  font-weight: 500;
  line-height: 1;
  white-space: nowrap;
}
</style>
