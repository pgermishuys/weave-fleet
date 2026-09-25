<script setup lang="ts">
import { computed } from "vue";
import CheckIcon from "@/components/github/CheckIcon.vue";
import { checkCountWords, checksFromCounts, checksHeadline, type CheckCounts } from "@/lib/pr-state";

/** GitHub's words for a pull request's checks, the counts, and a bar split into passed, failing and running. */
const props = withDefaults(defineProps<{
  counts: CheckCounts;
  /** Leave out the headline and counts, for rows that only need the bar. */
  barOnly?: boolean;
}>(), {
  barOnly: false,
});

const state = computed(() => checksFromCounts(props.counts));
const total = computed(() => props.counts.passed + props.counts.failing + props.counts.pending);
const iconState = computed(() => (state.value === "failing" ? "failure" : state.value === "pending" ? "pending" : "success"));
</script>

<template>
  <div
    class="checks-summary"
    data-testid="checks-summary"
  >
    <div
      v-if="!barOnly"
      class="checks-summary__head"
    >
      <CheckIcon :state="iconState" />
      <span class="checks-summary__headline">{{ checksHeadline(state) }}</span>
      <span class="checks-summary__counts">{{ checkCountWords(counts) }}</span>
    </div>
    <div
      v-if="total > 0"
      class="checks-summary__bar"
      role="img"
      :aria-label="checkCountWords(counts)"
    >
      <span
        v-if="counts.passed"
        class="checks-summary__segment"
        data-check="success"
        :style="{ flexGrow: counts.passed }"
      />
      <span
        v-if="counts.failing"
        class="checks-summary__segment"
        data-check="failure"
        :style="{ flexGrow: counts.failing }"
      />
      <span
        v-if="counts.pending"
        class="checks-summary__segment"
        data-check="pending"
        :style="{ flexGrow: counts.pending }"
      />
    </div>
  </div>
</template>

<style scoped>
.checks-summary {
  display: grid;
  gap: 8px;
  min-width: 0;
}

.checks-summary__head {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 4px 8px;
  min-width: 0;
  font-size: 12.5px;
}

.checks-summary__headline {
  color: var(--text);
  font-weight: 500;
}

.checks-summary__counts {
  margin-left: auto;
  flex-shrink: 0;
  color: var(--muted);
  font-size: 11.5px;
  font-variant-numeric: tabular-nums;
}

.checks-summary__bar {
  display: flex;
  gap: 2px;
  height: 5px;
  overflow: hidden;
  border-radius: 999px;
  background: color-mix(in srgb, var(--text) 8%, transparent);
}

.checks-summary__segment {
  flex-basis: 0;
  height: 100%;
}

.checks-summary__segment[data-check="success"] { background: var(--check-pass); }
.checks-summary__segment[data-check="failure"] { background: var(--check-fail); }
.checks-summary__segment[data-check="pending"] { background: var(--check-pending); }
</style>
