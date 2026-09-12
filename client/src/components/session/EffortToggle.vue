<script setup lang="ts">
import { computed } from "vue";
import type { EffortLevel } from "@/composables/use-draft-state";

interface Props {
  variants?: readonly string[];
}

const props = withDefaults(defineProps<Props>(), {
  variants: undefined,
});

const selectedEffort = defineModel<EffortLevel>({ required: true });

const defaultVariants: readonly string[] = ["low", "medium", "high"];
const effortOrder = computed(() => props.variants ?? defaultVariants);

const defaultLabels: Record<string, string> = {
  low: "Low",
  medium: "Medium",
  high: "High",
};

function capitalize(str: string): string {
  return str.charAt(0).toUpperCase() + str.slice(1);
}

const filledDots = computed(() => effortOrder.value.indexOf(selectedEffort.value) + 1);
const effortLabel = computed(() => defaultLabels[selectedEffort.value] ?? capitalize(selectedEffort.value));

function cycleEffort(): void {
  const currentIndex = effortOrder.value.indexOf(selectedEffort.value);
  const nextIndex = (currentIndex + 1) % effortOrder.value.length;
  selectedEffort.value = effortOrder.value[nextIndex] ?? effortOrder.value[0] ?? "medium";
}
</script>

<template>
  <button
    type="button"
    class="effort-toggle"
    :aria-label="`Reasoning effort: ${effortLabel}`"
    @click="cycleEffort"
  >
    <span class="effort-toggle__label">{{ effortLabel }}</span>
    <span
      class="effort-toggle__dots"
      aria-hidden="true"
    >
      <span
        v-for="dotIndex in effortOrder.length"
        :key="dotIndex"
        class="effort-dot"
        :class="{ filled: dotIndex <= filledDots }"
      />
    </span>
  </button>
</template>

<style scoped>
.effort-toggle {
  display: inline-flex;
  align-items: center;
  gap: 7px;
  height: 28px;
  padding: 0 8px;
  border: 0;
  border-radius: var(--radius-btn);
  background: transparent;
  color: var(--muted);
  cursor: pointer;
  transition: background var(--transition);
}

.effort-toggle:hover {
  background: color-mix(in srgb, var(--text) 6%, transparent);
}

.effort-toggle:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}

.effort-toggle__label {
  color: color-mix(in srgb, var(--text) 80%, transparent);
  font-size: 12px;
}

.effort-toggle__dots {
  display: flex;
  align-items: center;
  gap: 2px;
}

.effort-dot {
  width: 7px;
  height: 7px;
  border-radius: 50%;
  background: color-mix(in srgb, var(--muted) 35%, transparent);
}

.effort-dot.filled {
  background: var(--accent);
}
</style>
