<script setup lang="ts">
import { computed } from "vue";
import {
  CircleCheck,
  CircleDot,
  GitMerge,
  GitPullRequest,
  GitPullRequestClosed,
  GitPullRequestDraft,
} from "lucide-vue-next";
import type { PrState } from "@/lib/pr-state";

/**
 * A pull request's or issue's icon in its state's colour. An issue is "open", or "closed" (done, in the merged
 * colour, as GitHub shows a completed issue).
 */
const props = withDefaults(defineProps<{
  kind: "pull" | "issue";
  state: PrState;
  size?: number;
}>(), {
  size: 14,
});

const icon = computed(() => {
  if (props.kind === "issue") return props.state === "closed" || props.state === "merged" ? CircleCheck : CircleDot;
  switch (props.state) {
    case "merged": return GitMerge;
    case "closed": return GitPullRequestClosed;
    case "draft": return GitPullRequestDraft;
    default: return GitPullRequest;
  }
});

const tone = computed(() => (props.kind === "issue" && props.state === "closed" ? "merged" : props.state));
</script>

<template>
  <component
    :is="icon"
    :size="props.size"
    class="gh-item-icon"
    :data-pr="tone"
    aria-hidden="true"
  />
</template>

<style scoped>
.gh-item-icon {
  flex-shrink: 0;
}

.gh-item-icon[data-pr="open"] { color: var(--pr-open); }
.gh-item-icon[data-pr="draft"] { color: var(--pr-draft); }
.gh-item-icon[data-pr="blocked"] { color: var(--pr-blocked); }
.gh-item-icon[data-pr="merged"] { color: var(--pr-merged); }
.gh-item-icon[data-pr="closed"] { color: var(--pr-closed); }
</style>
