<script setup lang="ts">
import GitHubItemIcon from "@/components/github/GitHubItemIcon.vue";
import type { PrState } from "@/lib/pr-state";

/** The state in words on a tinted pill: "Checks failing", "Ready to merge", "Merged". */
withDefaults(defineProps<{
  state: PrState;
  label: string;
  kind?: "pull" | "issue";
}>(), {
  kind: "pull",
});
</script>

<template>
  <span
    class="pr-pill"
    :data-pr="kind === 'issue' && state === 'closed' ? 'merged' : state"
  >
    <GitHubItemIcon
      :kind="kind"
      :state="state"
      :size="13"
    />
    {{ label }}
  </span>
</template>

<style scoped>
.pr-pill {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  height: 22px;
  padding: 0 9px;
  border-radius: 999px;
  background: color-mix(in srgb, var(--pr-tone) 14%, transparent);
  color: var(--pr-tone);
  font-size: 12px;
  font-weight: 500;
  white-space: nowrap;
}

.pr-pill[data-pr="open"] { --pr-tone: var(--pr-open); }
.pr-pill[data-pr="draft"] { --pr-tone: var(--pr-draft); }
.pr-pill[data-pr="blocked"] { --pr-tone: var(--pr-blocked); }
.pr-pill[data-pr="merged"] { --pr-tone: var(--pr-merged); }
.pr-pill[data-pr="closed"] { --pr-tone: var(--pr-closed); }

.pr-pill :deep(.gh-item-icon) {
  color: inherit;
}
</style>
