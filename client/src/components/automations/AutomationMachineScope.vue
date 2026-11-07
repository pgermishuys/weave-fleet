<script setup lang="ts">
import { computed } from "vue";
import { provideMachineTarget, targetFor, useMachineTarget } from "@/lib/machine-target";
import { useMachinesStore } from "@/stores/machines";

/**
 * Makes the automation composer below ask the machine its runs go to (`machineId`, a machine in the list; null for
 * this one, the machine the view shows), as the new-session page does for its Machine chip: folders, harnesses, agents, models and workflows are
 * that machine's. `fixed` (the view shows a machine other than the live one, whose list this isn't) ignores `machineId`:
 * runs go to the machine the view shows. The composer is rebuilt when the machine changes; its state lives above, so nothing typed is lost.
 */
const props = defineProps<{ machineId: string | null; fixed?: boolean }>();

const machines = useMachinesStore();
// The machine the view shows; where no run goes elsewhere, this is the machine they go to.
const viewTarget = useMachineTarget();
const target = computed(() => {
  if (props.fixed) return viewTarget;
  const picked = props.machineId ? machines.entries.find((entry) => entry.key === props.machineId) : undefined;
  return picked ? targetFor(picked.connection) : viewTarget;
});
provideMachineTarget(() => target.value);
</script>

<template>
  <div
    :key="target.key"
    class="automation-machine-scope"
  >
    <slot />
  </div>
</template>

<style scoped>
.automation-machine-scope {
  display: contents;
}
</style>
