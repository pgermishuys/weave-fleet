<script setup lang="ts">
import { watch } from "vue";
import { useRouter, useSearch } from "@tanstack/vue-router";
import WorkflowDetailPanel from "@/components/workflows/WorkflowDetailPanel.vue";
import { useWorkflowsFeature } from "@/composables/use-workflows-feature";
import { useWorkflowsNav } from "@/composables/use-workflows-nav";
import { usePreferencesStore } from "@/stores/preferences";

const router = useRouter();
const search = useSearch({ from: "/workflows" });
const preferences = usePreferencesStore();
const nav = useWorkflowsNav();
const { isWorkflowsEnabled } = useWorkflowsFeature();

// Off, the page doesn't exist: go home once the switch is known to be off.
watch([() => preferences.hasFetched, isWorkflowsEnabled], ([fetched, enabled]) => {
  if (fetched && !enabled) void router.navigate({ to: "/" });
}, { immediate: true });

// A link names a workflow and the folder its Run box runs in. Nothing starts; the address goes back to plain
// /workflows so a reload doesn't undo what the person picked since.
watch(() => [search.value.workflow, search.value.folder] as const, ([workflow, folder]) => {
  if (!workflow && !folder) return;
  if (folder) nav.setFolder({ kind: "repository", path: folder }, true);
  if (workflow) nav.setActiveWorkflow(workflow);
  void router.navigate({ to: "/workflows", search: {}, replace: true });
}, { immediate: true });
</script>

<template>
  <section class="flex h-full flex-col overflow-hidden">
    <WorkflowDetailPanel v-if="isWorkflowsEnabled" />
  </section>
</template>
