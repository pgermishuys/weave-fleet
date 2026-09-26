<script setup lang="ts">
import { computed } from "vue";
import GitHubItemIcon from "@/components/github/GitHubItemIcon.vue";
import type { ChecksState, PrState } from "@/lib/pr-state";

/** A pull request's number in its state's colour, with a dot for its checks: what a sessions list row shows. */
const props = defineProps<{
  number: number;
  state: PrState;
  checks: ChecksState;
  /** The tooltip, e.g. "Pull request #187 · 1 failing · 2 threads". */
  description: string;
}>();

const showChecks = computed(() => props.checks !== "none" && (props.state === "open" || props.state === "blocked" || props.state === "draft"));
</script>

<template>
  <span
    class="pr-badge"
    :data-pr="state"
    :title="description"
    data-testid="pr-badge"
  >
    <GitHubItemIcon
      kind="pull"
      :state="state"
      :size="12"
    />
    <span class="pr-badge__number">#{{ number }}</span>
    <span
      v-if="showChecks"
      class="pr-badge__checks"
      :data-checks="checks"
      aria-hidden="true"
    />
    <span class="sr-only">{{ description }}</span>
  </span>
</template>

<style scoped>
.pr-badge {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  flex-shrink: 0;
  border-bottom: 1px solid transparent;
  font-family: var(--font-mono-stack);
  font-size: 11px;
  font-variant-numeric: tabular-nums;
  line-height: 1.3;
  white-space: nowrap;
}

.pr-badge[data-pr="open"] { color: var(--pr-open); }
.pr-badge[data-pr="draft"] { color: var(--pr-draft); }
.pr-badge[data-pr="blocked"] { color: var(--pr-blocked); }
.pr-badge[data-pr="merged"] { color: var(--pr-merged); }
.pr-badge[data-pr="closed"] { color: var(--pr-closed); }

.pr-badge__checks {
  width: 6px;
  height: 6px;
  margin-left: 2px;
  border-radius: 50%;
}

.pr-badge__checks[data-checks="passing"] { background: var(--check-pass); }
.pr-badge__checks[data-checks="failing"] { background: var(--check-fail); }
.pr-badge__checks[data-checks="pending"] { background: var(--check-pending); }
</style>
