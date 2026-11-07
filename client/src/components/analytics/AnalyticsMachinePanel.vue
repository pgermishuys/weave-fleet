<script setup lang="ts">
import AnalyticsNavPanel from "@/components/analytics/AnalyticsNavPanel.vue";
import MachineScope from "@/components/layout/MachineScope.vue";
import { useAnalyticsMachine } from "@/composables/use-analytics-machine";

// The analytics-menu asks the machine its own switch shows (counts, the project list, Export); rebuilt when it changes.
const machine = useAnalyticsMachine();
</script>

<template>
  <MachineScope
    :key="machine.key.value"
    :target="machine.target.value"
  >
    <AnalyticsNavPanel />
  </MachineScope>
</template>
