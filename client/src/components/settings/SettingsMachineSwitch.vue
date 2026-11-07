<script setup lang="ts">
import MachineSwitch from "@/components/layout/MachineSwitch.vue";
import { useSettingsMachine } from "@/composables/use-settings-machine";

/** "Settings for" in the settings menu's header: which machine the detail pane shows (see `MachineSwitch`). */
const settingsMachine = useSettingsMachine();
</script>

<template>
  <MachineSwitch
    :choice="settingsMachine"
    label="Settings"
    testid="settings"
  />
</template>
