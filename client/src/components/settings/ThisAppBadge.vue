<script setup lang="ts">
import { useSettingsMachine } from "@/composables/use-settings-machine";

/**
 * Marks a General or Labs row that belongs to this app (or the whole Fleet), not to a machine: with another machine
 * picked in Settings, the row keeps showing and changing this app's value, and this says so. Nothing on the machine
 * Settings shows reads it. Hidden while Settings shows the live machine, where the two are the same.
 */
const settingsMachine = useSettingsMachine();
</script>

<template>
  <span
    v-if="settingsMachine.enabled.value && settingsMachine.isOther.value"
    class="this-app-badge"
    :title="`Set for this app. ${settingsMachine.entry.value.name} doesn't read it, so it stays the same whichever machine Settings shows.`"
    data-testid="this-app-badge"
  >This app · not {{ settingsMachine.entry.value.name }}'s</span>
</template>

<style scoped>
.this-app-badge { display: inline-block; padding: 0 5px; border-radius: 4px; font-family: var(--font-mono, ui-monospace, monospace); font-size: 0.6875rem; font-weight: 500; line-height: 1.25rem; color: var(--muted); background: color-mix(in srgb, var(--text) 8%, transparent); white-space: nowrap; }
</style>
