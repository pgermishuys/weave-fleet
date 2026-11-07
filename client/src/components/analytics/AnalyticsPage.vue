<script setup lang="ts">
import { onUnmounted } from "vue";
import { useRouter } from "@tanstack/vue-router";
import AnalyticsContent from "@/components/analytics/AnalyticsContent.vue";
import MachineScope from "@/components/layout/MachineScope.vue";
import { resetAnalyticsMachine, useAnalyticsMachine } from "@/composables/use-analytics-machine";

// Every section asks the machine the menu's switch shows. Moving between sections keeps the pick; leaving Analytics
// forgets it, so the next visit opens on the machine you're on.
const machine = useAnalyticsMachine();
const router = useRouter();
onUnmounted(() => {
  if (!router.state.location.pathname.startsWith("/analytics")) resetAnalyticsMachine();
});
</script>

<template>
  <MachineScope
    :key="machine.key.value"
    :target="machine.target.value"
  >
    <AnalyticsContent />
  </MachineScope>
</template>
