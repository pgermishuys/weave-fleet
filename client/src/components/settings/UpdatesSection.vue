<script setup lang="ts">
import HarnessUpdatesGroup from "@/components/settings/HarnessUpdatesGroup.vue";
import MachineUpdatesGroup from "@/components/settings/MachineUpdatesGroup.vue";
import SystemSection from "@/components/settings/SystemSection.vue";
import WhatsNewSection from "@/components/settings/WhatsNewSection.vue";
import { useFleetUpdatesEnabled } from "@/composables/use-fleet-updates";
import { useAppShellStore } from "@/stores/app-shell";
import { storeToRefs } from "pinia";

/**
 * Settings → Updates & about: every version and update in one place. Fleet (and the desktop app), harnesses, machines
 * (only while Updates in Machines is on in Labs), then What's new.
 */
const { config } = storeToRefs(useAppShellStore());
const machinesShowUpdates = useFleetUpdatesEnabled();
</script>

<template>
  <div
    class="grid gap-6"
    data-testid="updates-section"
  >
    <SystemSection />
    <HarnessUpdatesGroup v-if="!config.cloudMode" />
    <MachineUpdatesGroup v-if="machinesShowUpdates" />
    <WhatsNewSection />
  </div>
</template>
