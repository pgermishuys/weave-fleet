<script setup lang="ts">
import { computed } from "vue";
import type { EffortLevel } from "@/composables/use-draft-state";

const selectedEffort = defineModel<EffortLevel>({ required: true });

const effortOrder: readonly EffortLevel[] = ["low", "medium", "high"];
const effortLabels: Record<EffortLevel, string> = {
  low: "Low",
  medium: "Medium",
  high: "High",
};

const filledDots = computed(() => effortOrder.indexOf(selectedEffort.value) + 1);
const effortLabel = computed(() => effortLabels[selectedEffort.value]);

function cycleEffort(): void {
  const currentIndex = effortOrder.indexOf(selectedEffort.value);
  const nextIndex = (currentIndex + 1) % effortOrder.length;
  selectedEffort.value = effortOrder[nextIndex] ?? "medium";
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
        v-for="dotIndex in 3"
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
  gap: 8px;
  padding: 4px 8px;
  border: 1px solid var(--border);
  border-radius: 20px;
  background: transparent;
  color: var(--muted);
  cursor: pointer;
}

.effort-toggle:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}

.effort-toggle__label {
  color: var(--text);
  font-size: 11px;
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
  background: #3f3f46;
}

.effort-dot.filled {
  background: var(--accent);
}
</style>
