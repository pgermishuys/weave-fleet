<script setup lang="ts">
import { computed } from "vue";
import { effortLabel as effortLabelOf } from "@/lib/effort-memory";
import type { EffortLevel } from "@/composables/use-draft-state";

interface Props {
  variants?: readonly string[];
  /** The model's name, for the tooltip "High, remembered for Opus 5.5". */
  rememberedFor?: string;
  /** A short note after a model switch, "Opus 5.5: back on High". */
  note?: string;
}

const props = withDefaults(defineProps<Props>(), {
  variants: undefined,
  rememberedFor: undefined,
  note: "",
});

const selectedEffort = defineModel<EffortLevel>({ required: true });

const defaultVariants: readonly string[] = ["low", "medium", "high"];
const effortOrder = computed(() => props.variants ?? defaultVariants);

const filledDots = computed(() => effortOrder.value.indexOf(selectedEffort.value) + 1);
const effortLabel = computed(() => effortLabelOf(selectedEffort.value));
const tooltip = computed(() => {
  if (!selectedEffort.value) return "Default: the harness's own level, nothing sent";
  return props.rememberedFor ? `${effortLabel.value}, remembered for ${props.rememberedFor}` : effortLabel.value;
});

// low > medium > high (the model's own levels) > Default ("") > low
function cycleEffort(): void {
  const order = [...effortOrder.value, ""];
  const currentIndex = order.indexOf(selectedEffort.value);
  selectedEffort.value = order[(currentIndex + 1) % order.length] ?? "";
}
</script>

<template>
  <button
    type="button"
    class="effort-toggle"
    :aria-label="`Reasoning effort: ${effortLabel}`"
    :title="tooltip"
    @click="cycleEffort"
  >
    <span
      v-if="note"
      class="effort-toggle__note"
      role="status"
    >{{ note }}</span>
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

.effort-toggle__note {
  color: var(--accent);
  font-size: 11px;
  white-space: nowrap;
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
