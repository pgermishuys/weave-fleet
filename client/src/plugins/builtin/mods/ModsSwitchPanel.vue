<script setup lang="ts">
import { computed } from "vue";
import ModsRuntimePanel from "@/components/settings/mods/ModsRuntimePanel.vue";
import { useModsStore } from "@/stores/mods";

// What sits under the Mods plugin's switch: Fleet asking before it installs Bun, the install's progress, and the Bun in use.
const mods = useModsStore();
const enabled = computed(() => mods.isSwitchedOn);
</script>

<template>
  <ModsRuntimePanel :enabled="enabled" />
</template>
