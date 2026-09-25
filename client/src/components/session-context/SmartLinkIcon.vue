<script setup lang="ts">
import { computed } from "vue";
import GitHubItemIcon from "@/components/github/GitHubItemIcon.vue";
import { prState } from "@/lib/pr-state";
import { isPullRequest, linkPrFacts, type SmartLink } from "@/lib/smart-links";

const props = withDefaults(defineProps<{
  link: SmartLink;
  size?: number;
}>(), {
  size: 14,
});

const kind = computed(() => (isPullRequest(props.link) ? "pull" : "issue"));
const state = computed(() => {
  if (kind.value === "issue") return props.link.status === "closed" ? "closed" : "open";
  return prState(linkPrFacts(props.link));
});
// Before GitHub has answered, the icon stays neutral rather than guessing "open".
const resolved = computed(() => props.link.enrichmentStatus === "resolved");
</script>

<template>
  <GitHubItemIcon
    :kind="kind"
    :state="resolved ? state : 'draft'"
    :size="props.size"
    class="smart-link-icon"
    :data-resolved="resolved"
  />
</template>
