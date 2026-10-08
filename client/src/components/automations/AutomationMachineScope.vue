<script setup lang="ts">
import { computed } from "vue";
import { liveTarget, provideMachineTarget, targetFor } from "@/lib/machine-target";
import { useMachinesStore } from "@/stores/machines";

/**
 * Makes the automation composer below ask the machine its runs go to (`machineId`, a machine in the list; null for
 * this one), as the new-session page does for its Machine chip: folders, harnesses, agents, models and workflows are
 * that machine's. The composer is rebuilt when the machine changes; its state lives above, so nothing typed is lost.
 */
const props = defineProps<{ machineId: string | null }>();

const machines = useMachinesStore();
const target = computed(() => {
  const picked = props.machineId ? machines.entries.find((entry) => entry.key === props.machineId) : undefined;
  return picked ? targetFor(picked.connection) : liveTarget();
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
