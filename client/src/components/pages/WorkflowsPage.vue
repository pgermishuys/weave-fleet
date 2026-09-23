<script setup lang="ts">
import { watch } from "vue";
import { useRouter } from "@tanstack/vue-router";
import WorkflowDetailPanel from "@/components/workflows/WorkflowDetailPanel.vue";
import { useWorkflowsFeature } from "@/composables/use-workflows-feature";
import { usePreferencesStore } from "@/stores/preferences";

const router = useRouter();
const preferences = usePreferencesStore();
const { isWorkflowsEnabled } = useWorkflowsFeature();

// Off, the page doesn't exist: go home once the switch is known to be off.
watch([() => preferences.hasFetched, isWorkflowsEnabled], ([fetched, enabled]) => {
  if (fetched && !enabled) void router.navigate({ to: "/" });
}, { immediate: true });
</script>

<template>
  <section class="flex h-full flex-col overflow-hidden">
    <WorkflowDetailPanel v-if="isWorkflowsEnabled" />
  </section>
</template>
