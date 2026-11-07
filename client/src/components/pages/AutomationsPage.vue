<script setup lang="ts">
import AutomationDetailPanel from "@/components/automations/AutomationDetailPanel.vue";
import MachineScope from "@/components/layout/MachineScope.vue";
import { resetAutomationsMachine, useAutomationsMachine } from "@/composables/use-automations-machine";

// Automations opens on the machine you're on; its detail pane asks the machine the list's switch shows.
resetAutomationsMachine();
const automationsMachine = useAutomationsMachine();
</script>

<template>
  <section class="flex h-full flex-col overflow-hidden">
    <MachineScope
      :key="automationsMachine.key.value"
      :target="automationsMachine.target.value"
    >
      <AutomationDetailPanel />
    </MachineScope>
  </section>
</template>
