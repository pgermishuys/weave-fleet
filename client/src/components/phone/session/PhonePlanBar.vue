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
    class="ppb"
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
    <span class="ppb__text">{{ next ? `Next: ${next}` : "Plan done" }}</span>
    <span class="ppb__count">{{ progress.done }}/{{ progress.total }}</span>
    <ChevronRight
      :size="14"
      aria-hidden="true"
    />
  </button>
</template>

<style scoped>
.ppb {
  display: flex;
  flex: none;
  align-items: center;
  gap: 8px;
  min-height: 40px;
  margin: 6px 12px 0;
  padding: 6px 10px;
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  background: var(--card-bg);
  color: var(--text);
  font: inherit;
  font-size: 12px;
  text-align: left;
  cursor: pointer;
}

.ppb__track {
  width: 28px;
  height: 4px;
  flex: none;
  overflow: hidden;
  border-radius: 999px;
  background: var(--border);
}

.ppb__fill {
  display: block;
  height: 100%;
  background: var(--accent);
}

.ppb__text {
  flex: 1;
  overflow: hidden;
  color: var(--muted);
  white-space: nowrap;
  text-overflow: ellipsis;
}

.ppb__count {
  font-weight: 600;
  font-variant-numeric: tabular-nums;
}
</style>
