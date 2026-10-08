<script setup lang="ts">
import { provideMachineTarget, type MachineTarget } from "@/lib/machine-target";

/**
 * Makes what's inside ask `target`'s machine (`useMachineTarget`). It's read once, as everything inside reads it
 * once when it sets up, so key this by the machine (`:key="target.key"`) to rebuild them when it changes.
 */
const props = defineProps<{ target: MachineTarget }>();
provideMachineTarget(() => props.target);
</script>

<template>
  <slot />
</template>
