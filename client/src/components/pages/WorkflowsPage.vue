<script setup lang="ts">
import { watch } from "vue";
import { useRouter, useSearch } from "@tanstack/vue-router";
import FeatureOffNotice from "@/components/pages/FeatureOffNotice.vue";
import WorkflowModelRoles from "@/components/workflows/WorkflowModelRoles.vue";
import WorkflowDetailPanel from "@/components/workflows/WorkflowDetailPanel.vue";
import { useWorkflowsFeature } from "@/composables/use-workflows-feature";
import { useWorkflowsNav } from "@/composables/use-workflows-nav";
import { useWorkflowsStore } from "@/stores/workflows";

const router = useRouter();
const search = useSearch({ from: "/workflows" });
const nav = useWorkflowsNav();
const store = useWorkflowsStore();
const { isWorkflowsEnabled, setWorkflowsEnabled } = useWorkflowsFeature();

// The switch flips before the server has saved it, so what loaded in that gap was refused: load again once it's saved.
async function turnOn(): Promise<void> {
  await setWorkflowsEnabled(true);
  await Promise.all([nav.reload(), store.ensureLoaded()]);
}

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
    <template v-if="isWorkflowsEnabled">
      <WorkflowDetailPanel />
      <WorkflowModelRoles />
    </template>
    <FeatureOffNotice
      v-else
      title="Workflows"
      description="A workflow is a recipe of steps for an agent, like fixing a bug or reviewing a change, that you can run again and again."
      :turn-on="turnOn"
    />
  </section>
</template>
