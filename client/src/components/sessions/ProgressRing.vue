<script setup lang="ts">
import { computed } from "vue";

/** A small ring that fills as work gets done. Turns green when everything is done. */
const props = withDefaults(defineProps<{
  done: number;
  total: number;
  size?: number;
  strokeWidth?: number;
}>(), {
  size: 14,
  strokeWidth: 2,
});

const radius = computed(() => (props.size - props.strokeWidth) / 2);
const circumference = computed(() => 2 * Math.PI * radius.value);
const ratio = computed(() => (props.total > 0 ? Math.min(1, props.done / props.total) : 0));
const offset = computed(() => circumference.value * (1 - ratio.value));
const complete = computed(() => props.total > 0 && props.done >= props.total);
</script>

<template>
  <svg
    class="progress-ring"
    :class="{ 'progress-ring--complete': complete }"
    :width="size"
    :height="size"
    :viewBox="`0 0 ${size} ${size}`"
    aria-hidden="true"
  >
    <circle
      class="progress-ring__track"
      :cx="size / 2"
      :cy="size / 2"
      :r="radius"
      :stroke-width="strokeWidth"
    />
    <circle
      class="progress-ring__value"
      :cx="size / 2"
      :cy="size / 2"
      :r="radius"
      :stroke-width="strokeWidth"
      :stroke-dasharray="circumference"
      :stroke-dashoffset="offset"
    />
  </svg>
</template>

<style scoped>
.progress-ring {
  flex-shrink: 0;
  display: block;
  transform: rotate(-90deg);
}

.progress-ring__track {
  fill: none;
  stroke: color-mix(in srgb, var(--text) 14%, transparent);
}

.progress-ring__value {
  fill: none;
  stroke: var(--accent);
  stroke-linecap: round;
  transition: stroke-dashoffset var(--transition);
}

.progress-ring--complete .progress-ring__value {
  stroke: var(--running);
}
</style>
