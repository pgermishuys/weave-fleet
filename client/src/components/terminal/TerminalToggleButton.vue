<script setup lang="ts">
import { computed } from "vue";
import { SquareTerminal } from "lucide-vue-next";
import { Button } from "@/components/ui/button";
import { formatShortcut } from "@/lib/keybinding-utils";
import { useAppShellStore } from "@/stores/app-shell";
import { useKeybindingsStore } from "@/stores/keybindings";
import { useTerminalsStore } from "@/stores/terminals";

/** The session header's terminal button. A dot shows while shells run with the drawer hidden. */

const props = defineProps<{ sessionId: string }>();

const store = useTerminalsStore();
const appShell = useAppShellStore();
const keybindings = useKeybindingsStore();

const isMac = typeof navigator !== "undefined" && navigator.platform.toUpperCase().includes("MAC");

const open = computed(() => store.isOpen(props.sessionId));
const running = computed(() => !open.value && store.terminalsFor(props.sessionId).length > 0);
const shortcut = computed(() => {
  const binding = keybindings.bindings["toggle-terminal"]?.globalShortcut;
  return binding ? ` (${formatShortcut(binding, isMac)})` : "";
});
const label = computed(() => {
  if (open.value) return `Hide terminal${shortcut.value}`;
  return running.value ? `Show terminal: shells are running${shortcut.value}` : `Show terminal${shortcut.value}`;
});
</script>

<template>
  <Button
    v-if="appShell.config.terminalEnabled"
    variant="toolbar-icon"
    size="toolbar"
    class="terminal-toggle"
    data-testid="terminal-toggle"
    :aria-pressed="open"
    :aria-label="label"
    :title="label"
    @click="store.toggleOpen(props.sessionId)"
  >
    <SquareTerminal aria-hidden="true" />
    <span
      v-if="running"
      class="terminal-toggle__dot"
      aria-hidden="true"
    />
  </Button>
</template>

<style scoped>
.terminal-toggle {
  position: relative;
}

.terminal-toggle[aria-pressed="true"] {
  background-color: color-mix(in srgb, var(--text) 9%, transparent);
  color: var(--text);
}

.terminal-toggle__dot {
  position: absolute;
  top: 5px;
  right: 5px;
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: var(--running);
  box-shadow: 0 0 0 2px var(--panel-bg);
}
</style>
