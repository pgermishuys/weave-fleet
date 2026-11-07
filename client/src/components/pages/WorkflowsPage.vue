<script setup lang="ts">
import { watch } from "vue";
import { useRouter, useSearch } from "@tanstack/vue-router";
import MachineScope from "@/components/layout/MachineScope.vue";
import WorkflowsPane from "@/components/workflows/WorkflowsPane.vue";
import { resetWorkflowsMachine, useWorkflowsMachine } from "@/composables/use-workflows-machine";
import { useWorkflowsNav } from "@/composables/use-workflows-nav";

const router = useRouter();
const search = useSearch({ from: "/workflows" });
// Workflows opens on the machine you're on; the list's switch picks the machine its detail pane shows.
resetWorkflowsMachine();
const workflowsMachine = useWorkflowsMachine();
const nav = useWorkflowsNav();

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
    <MachineScope
      :key="workflowsMachine.key.value"
      :target="workflowsMachine.target.value"
    >
      <WorkflowsPane />
    </MachineScope>
  </section>
</template>
