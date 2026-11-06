<script setup lang="ts">
import { useSettingsNav } from "@/composables/use-settings-nav";

// The Mods plugin's own page: the switch and the Bun it needs sit above (the plugin page draws them); the mods are in Skills & tools.
defineOptions({ inheritAttrs: false });

const { setActiveSection } = useSettingsNav();
</script>

<template>
  <div
    class="rounded-card border border-border bg-card-bg p-4 text-sm text-muted"
    data-testid="mods-plugin-page"
  >
    Agents can write small add-ons for Fleet: what a tool row shows, buttons, panes you open from the + menu, and hooks on
    prompts, turns and permission asks. You review each one before it's kept. Your mods, and Fleet's samples to add, are in
    <button
      type="button"
      class="text-accent hover:underline focus-visible:outline focus-visible:outline-2 focus-visible:outline-accent"
      data-testid="mods-plugin-open-mods"
      @click="setActiveSection('skills', 'mods')"
    >Skills &amp; tools › Mods</button>.
  </div>
</template>
