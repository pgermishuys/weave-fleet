<script setup lang="ts">
import { useEnabledHarnesses } from "@/composables/use-enabled-harnesses";

/** Until a harness is ready on this phone's machine, say so: the check under "Needs you" must not read as all well. */
const { noHarnessReason } = useEnabledHarnesses();
</script>

<template>
  <div
    v-if="noHarnessReason !== null"
    class="ph-banner ph-fade-in"
    role="status"
    data-testid="phone-no-harness"
  >
    <strong class="block">No harness is ready yet</strong>
    Fleet runs sessions through a harness such as OpenCode or Claude Code. Install one to start. Set it up in Fleet on that machine.
  </div>
</template>
