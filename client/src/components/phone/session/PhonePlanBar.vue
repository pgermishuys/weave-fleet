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
  <div class="ph-tools">
    <button
      type="button"
      class="ph-tool ppb"
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
      <span class="ph-tool__d ppb__text">{{ next ? `Next: ${next}` : "Plan done" }}</span>
      <span class="ph-tool__r">{{ progress.done }}/{{ progress.total }}</span>
      <ChevronRight
        class="ph-tool__ic"
        aria-hidden="true"
      />
    </button>
  </div>
</template>

<style scoped>
.ppb__track {
  width: 32px;
  height: 5px;
  flex: none;
  overflow: hidden;
  border-radius: var(--radius-btn);
  background: var(--ph-tint-9);
}

.ppb__fill {
  display: block;
  height: 100%;
  background: var(--accent);
}

.ppb__text {
  font-family: var(--ph-font);
  font-size: 14px;
  color: var(--text);
}
</style>
