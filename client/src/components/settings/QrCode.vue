<script setup lang="ts">
import { computed } from "vue";
import { encode } from "uqr";

/**
 * A QR code as SVG, dark on white whatever the theme: phone cameras read that best.
 */
const props = withDefaults(defineProps<{ value: string; size?: number; label?: string }>(), {
  size: 168,
  label: "QR code",
});

const qr = computed(() => encode(props.value, { ecc: "M", border: 2 }));

const path = computed(() => {
  const parts: string[] = [];
  qr.value.data.forEach((row, y) => {
    row.forEach((dark, x) => {
      if (dark) parts.push(`M${x} ${y}h1v1h-1z`);
    });
  });
  return parts.join("");
});
</script>

<template>
  <svg
    class="qr-code"
    role="img"
    :aria-label="label"
    :width="size"
    :height="size"
    :viewBox="`0 0 ${qr.size} ${qr.size}`"
    shape-rendering="crispEdges"
  >
    <rect
      :width="qr.size"
      :height="qr.size"
      fill="#ffffff"
    />
    <path
      :d="path"
      fill="#111111"
    />
  </svg>
</template>

<style scoped>
.qr-code {
  display: block;
  flex-shrink: 0;
  border-radius: var(--radius-btn);
}
</style>
