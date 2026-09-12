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
import { isPullRequest, type SmartLink } from "@/lib/smart-links";

const props = withDefaults(defineProps<{
  link: SmartLink;
  size?: number;
}>(), {
  size: 14,
});

const icon = computed(() => {
  if (isPullRequest(props.link)) {
    switch (props.link.status) {
      case "merged": return GitMerge;
      case "closed": return GitPullRequestClosed;
      case "draft": return GitPullRequestDraft;
      default: return GitPullRequest;
    }
  }
  return props.link.status === "closed" ? CircleCheck : CircleDot;
});

// Before GitHub has answered, the icon stays neutral rather than guessing "open".
const tone = computed(() => {
  if (props.link.enrichmentStatus !== "resolved") return "unknown";
  if (props.link.status === "merged") return "merged";
  if (props.link.status === "closed") return isPullRequest(props.link) ? "closed" : "done";
  if (props.link.status === "draft") return "draft";
  return "open";
});
</script>

<template>
  <component
    :is="icon"
    :size="props.size"
    class="smart-link-icon"
    :data-tone="tone"
    aria-hidden="true"
  />
</template>

<style scoped>
.smart-link-icon {
  flex-shrink: 0;
  color: var(--muted);
}

.smart-link-icon[data-tone="open"] {
  color: var(--running);
}

.smart-link-icon[data-tone="merged"],
.smart-link-icon[data-tone="done"] {
  color: var(--queued);
}

.smart-link-icon[data-tone="closed"] {
  color: var(--error);
}
</style>
