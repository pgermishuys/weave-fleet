<script setup lang="ts">
import { computed } from "vue";
import { CircleCheck, CircleDashed, CircleX, LoaderCircle, Minus } from "lucide-vue-next";

/** One check's state: success, failure, pending (spinning), skipped or neutral. */
const props = withDefaults(defineProps<{
  state: "success" | "failure" | "pending" | "skipped" | "neutral";
  size?: number;
}>(), {
  size: 14,
});

const icon = computed(() => {
  switch (props.state) {
    case "success": return CircleCheck;
    case "failure": return CircleX;
    case "pending": return LoaderCircle;
    case "skipped": return CircleDashed;
    default: return Minus;
  }
});
</script>

<template>
  <component
    :is="icon"
    :size="size"
    class="check-icon"
    :data-check="state"
    aria-hidden="true"
  />
</template>

<style scoped>
.check-icon {
  flex-shrink: 0;
  color: var(--muted);
}

.check-icon[data-check="success"] { color: var(--check-pass); }
.check-icon[data-check="failure"] { color: var(--check-fail); }

.check-icon[data-check="pending"] {
  color: var(--check-pending);
  animation: check-icon-spin 1s linear infinite;
}

@keyframes check-icon-spin {
  to { transform: rotate(360deg); }
}

@media (prefers-reduced-motion: reduce) {
  .check-icon[data-check="pending"] { animation: none; }
}
</style>
