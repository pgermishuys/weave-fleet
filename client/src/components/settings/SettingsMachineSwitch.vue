<script setup lang="ts">
import { ref } from "vue";
import { Check, ChevronDown } from "lucide-vue-next";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { useSettingsMachine } from "@/composables/use-settings-machine";
import { useMachinesStore } from "@/stores/machines";
import { useSessionsStore } from "@/stores/sessions";

/**
 * "Settings for" in the settings menu's header: which machine the detail pane shows. It swaps the pane only, never
 * the menu. Each machine lists its session count; the one shown has a check.
 */
const machines = useMachinesStore();
const sessions = useSessionsStore();
const settingsMachine = useSettingsMachine();
const open = ref(false);

function countOf(key: string): number {
  return key === machines.liveKey ? sessions.sessions.length : machines.others[key]?.sessions.length ?? 0;
}

function choose(key: string): void {
  settingsMachine.pick(key);
  open.value = false;
}
</script>

<template>
  <Popover v-model:open="open">
    <PopoverTrigger as-child>
      <button
        type="button"
        class="mx-switch"
        aria-label="Settings for machine"
        data-testid="settings-machine-switch"
      >
        <span class="mx-dot" aria-hidden="true" />
        <span class="mx-name">{{ settingsMachine.entry.value.name }}</span>
        <ChevronDown
          class="mx-chev"
          aria-hidden="true"
        />
      </button>
    </PopoverTrigger>
    <PopoverContent
      align="start"
      class="mx-menu"
    >
      <p class="mx-menu-label">
        Settings for
      </p>
      <button
        v-for="entry in machines.entries"
        :key="entry.key"
        type="button"
        class="mx-item"
        :data-testid="`settings-machine-${entry.key}`"
        @click="choose(entry.key)"
      >
        <span class="mx-dot" aria-hidden="true" />
        <span class="mx-item-name">{{ entry.name }} <small>{{ countOf(entry.key) }} {{ countOf(entry.key) === 1 ? "session" : "sessions" }}</small></span>
        <Check
          v-if="entry.key === settingsMachine.key.value"
          class="mx-check"
          aria-label="Shown"
        />
      </button>
    </PopoverContent>
  </Popover>
</template>

<style scoped>
.mx-switch { display: inline-flex; align-items: center; gap: 6px; max-width: 100%; padding: 3px 7px 3px 8px; border-radius: 6px; border: 1px solid var(--border); background: transparent; cursor: pointer; font: inherit; }
.mx-switch:hover, .mx-switch[aria-expanded="true"] { background: color-mix(in srgb, var(--text) 6%, transparent); }
.mx-switch:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
.mx-dot { width: 7px; height: 7px; border-radius: 50%; background: var(--running); flex-shrink: 0; }
.mx-name { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-family: var(--font-mono, ui-monospace, monospace); font-size: 0.82143rem; color: var(--coral); }
.mx-chev { width: 12px; height: 12px; flex-shrink: 0; color: var(--muted); }
.mx-menu { min-width: 240px; padding: 4px; }
.mx-menu-label { margin: 0; padding: 6px 8px 4px; font-size: 0.71429rem; letter-spacing: 0.06em; text-transform: uppercase; color: var(--muted); }
.mx-item { display: grid; grid-template-columns: 7px 1fr auto; align-items: center; gap: 8px; width: 100%; padding: 7px 8px; border: 0; border-radius: 6px; background: transparent; cursor: pointer; text-align: left; font: inherit; color: var(--text); }
.mx-item:hover { background: color-mix(in srgb, var(--text) 7%, transparent); }
.mx-item small { margin-left: 4px; color: var(--muted); font-size: 0.78571rem; }
.mx-check { width: 12px; height: 12px; color: var(--accent); }
</style>
