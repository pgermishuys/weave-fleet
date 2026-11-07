<script setup lang="ts">
import FeatureOffNotice from "@/components/pages/FeatureOffNotice.vue";
import WorkflowModelRoles from "@/components/workflows/WorkflowModelRoles.vue";
import WorkflowDetailPanel from "@/components/workflows/WorkflowDetailPanel.vue";
import { useWorkflowsFeature } from "@/composables/use-workflows-feature";
import { useWorkflowsNav } from "@/composables/use-workflows-nav";
import { useMachineTarget } from "@/lib/machine-target";
import { useWorkflowsStore } from "@/stores/workflows";

// Made inside the page's MachineScope, which is rebuilt when the machine changes: the machine read here is the one
// the view shows, so Workflows being on or off, the runs and Turn on are that machine's.
const target = useMachineTarget();
const nav = useWorkflowsNav();
const store = useWorkflowsStore(target);
const { isWorkflowsEnabled, setWorkflowsEnabled } = useWorkflowsFeature(target);

// The switch flips before the server has saved it, so what loaded in that gap was refused: load again once it's saved.
async function turnOn(): Promise<void> {
  await setWorkflowsEnabled(true);
  await Promise.all([nav.reload(), store.ensureLoaded()]);
}
</script>

<template>
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
</template>
