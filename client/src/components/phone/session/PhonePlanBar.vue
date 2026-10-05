<script setup lang="ts">
import { computed } from "vue";
import { ChevronRight } from "lucide-vue-next";
import { currentPlanStep, type SessionProgressDetail } from "@/lib/session-progress";

/** "Next: run the contract tests · 3/5" under the header while the session works through a plan or todos. */
const props = defineProps<{ progress: SessionProgressDetail }>();
const emit = defineEmits<{ (event: "open"): void }>();

const next = computed(() => {
  if (props.progress.plan) {
    const step = currentPlanStep(props.progress.plan);
    if (step) return step.step.title;
  }
  return props.progress.current ?? null;
});
const share = computed(() => props.progress.total ? Math.round((props.progress.done / props.progress.total) * 100) : 0);
</script>

<template>
  <button
    type="button"
    class="ph-step ppb"
    data-testid="phone-plan-bar"
    @click="emit('open')"
  >
    <span
      class="ppb__track"
      aria-hidden="true"
    ><span
      class="ppb__fill"
      :style="{ width: `${share}%` }"
    /></span>
    <span class="ph-step__t ppb__text">{{ next ? `Next: ${next}` : "Plan done" }}</span>
    <span class="ppb__count">{{ progress.done }}/{{ progress.total }}</span>
    <ChevronRight
      class="ph-row__chev"
      :size="16"
      :stroke-width="3"
      aria-hidden="true"
    />
  </button>
</template>

<style scoped>
.ppb__track {
  width: 32px;
  height: 5px;
  flex: none;
  overflow: hidden;
  border-radius: 3px;
  background: var(--ph-fill-strong);
}

.ppb__fill {
  display: block;
  height: 100%;
  background: var(--accent);
}

.ppb__text {
  color: var(--muted);
}

.ppb__count {
  font-weight: 600;
  font-variant-numeric: tabular-nums;
}
</style>
