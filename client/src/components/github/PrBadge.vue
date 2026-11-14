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

/* Row text: each state's colour keeps as much of itself as still reads at 4.5:1 on every session row background,
   the rest is --text (--row-* in main.css); a draft is the row's quiet meta colour. */
.pr-badge[data-pr="open"] { color: color-mix(in srgb, var(--pr-open) var(--row-running), var(--text)); }
.pr-badge[data-pr="draft"] { color: color-mix(in srgb, var(--text) var(--row-meta), transparent); }
.pr-badge[data-pr="blocked"] { color: color-mix(in srgb, var(--pr-blocked) var(--row-waiting), var(--text)); }
.pr-badge[data-pr="merged"] { color: color-mix(in srgb, var(--pr-merged) var(--row-queued), var(--text)); }
.pr-badge[data-pr="closed"] { color: color-mix(in srgb, var(--pr-closed) var(--row-error), var(--text)); }

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
