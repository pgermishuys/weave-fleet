<script setup lang="ts">
import { computed } from "vue";
import { storeToRefs } from "pinia";
import NoticeChips from "@/components/notices/NoticeChips.vue";
import RunningWorkCounter from "@/components/layout/RunningWorkCounter.vue";
import UsageLimitChip from "@/components/layout/UsageLimitChip.vue";
import { useAppShellStore } from "@/stores/app-shell";
import { useCommandStore } from "@/stores/commands";
import { useSessionsStore } from "@/stores/sessions";
import { useTerminalsStore } from "@/stores/terminals";

const sessionsStore = useSessionsStore();
const { activeSessionId } = storeToRefs(sessionsStore);
const appShell = useAppShellStore();
const commandStore = useCommandStore();
const { focused: terminalFocused } = storeToRefs(useTerminalsStore());

const isMac = navigator.platform.toUpperCase().indexOf("MAC") >= 0;
const mod = isMac ? "⌘" : "Ctrl";
/** The shortcut as `aria-keyshortcuts` spells it, e.g. `Control+K`. */
const modKey = isMac ? "Meta" : "Control";

const activeSession = computed(() =>
  sessionsStore.sessionById(activeSessionId.value),
);

/**
 * Each hint is a button that runs the same command as its shortcut. A command that isn't there (yet) or is disabled
 * leaves its button disabled, with the command's own reason as the title.
 */
function commandHint(id: string, shortcut: string) {
  return computed(() => {
    const command = commandStore.getCommand(id);
    if (!command) return { disabled: true, label: "", title: "Not available yet" };
    return {
      disabled: command.disabled === true,
      label: command.label,
      title: command.disabled ? (command.description ?? command.label) : `${command.label} (${shortcut})`,
    };
  });
}

const previousSessionHint = commandHint("nav-prev-session", `${mod} [`);
const nextSessionHint = commandHint("nav-next-session", `${mod} ]`);
const sidebarHint = commandHint("toggle-sidebar", `${mod} B`);
const terminalHint = commandHint("toggle-terminal", `${mod} J`);

/** Working the way the composer's Interrupt button reads it: a turn, a delegation or a retry is under way. */
const activeSessionWorking = computed(() => {
  const activity = activeSession.value?.activityStatus;
  return activity === "busy" || activity === "delegating" || activity === "retry";
});

const interruptHint = computed(() => {
  const command = commandStore.getCommand("interrupt-session");
  if (!command || command.disabled || !activeSession.value) {
    return { disabled: true, title: "Open a session to interrupt it" };
  }
  if (!activeSessionWorking.value) {
    return { disabled: true, title: "Nothing to interrupt: the session isn't working" };
  }
  return { disabled: false, title: "Interrupt the session (Esc)" };
});

const modelBadge = computed(() => {
  const harnessType = activeSession.value?.harnessType;
  // Mock model badge - in real implementation this would come from session metadata
  return harnessType || "claude-opus-4";
});

const tokenCount = computed(() => {
  const tokens = activeSession.value?.totalTokens;
  if (!tokens) {
    return "0 tokens";
  }
  return `${tokens.toLocaleString()} tokens`;
});
</script>

<template>
  <footer class="status-bar">
    <!--
      The hints are buttons that run what their shortcut runs. mousedown.prevent keeps the keyboard where it was (the
      composer, the terminal), so the next shortcut still does what it says; Tab still reaches them.
    -->
    <div
      v-if="terminalFocused"
      class="status-bar__left"
      data-testid="terminal-keyboard-hint"
    >
      <span class="terminal-owner">Terminal has the keyboard</span>
      <span class="shortcut-separator">·</span>
      <span class="shortcut-hint">
        <kbd>Esc</kbd> <kbd>{{ mod }} K</kbd> <kbd>{{ mod }} B</kbd> go to the shell
      </span>
      <span class="shortcut-separator">·</span>
      <button
        type="button"
        class="shortcut-hint"
        data-testid="status-hint-hide-terminal"
        :disabled="terminalHint.disabled"
        :title="terminalHint.title"
        aria-label="Hide terminal"
        :aria-keyshortcuts="`${modKey}+J`"
        @mousedown.prevent
        @click="commandStore.runCommand('toggle-terminal')"
      >
        <kbd>{{ mod }} J</kbd> Hide terminal
      </button>
      <span class="shortcut-separator">·</span>
      <span class="shortcut-hint">
        <kbd>{{ isMac ? "⌘ C" : "Ctrl Shift C" }}</kbd> Copy
      </span>
    </div>
    <div
      v-else
      class="status-bar__left"
    >
      <button
        type="button"
        class="shortcut-hint"
        data-testid="status-hint-palette"
        :title="`Command palette (${mod} K)`"
        aria-label="Command palette"
        :aria-keyshortcuts="`${modKey}+K`"
        @mousedown.prevent
        @click="commandStore.togglePalette()"
      >
        <kbd>{{ mod }} K</kbd> Command palette
      </button>
      <span class="shortcut-separator">·</span>
      <span class="shortcut-hint shortcut-hint--pair">
        <kbd>{{ mod }}</kbd>
        <button
          type="button"
          class="shortcut-key"
          data-testid="status-hint-prev-session"
          :disabled="previousSessionHint.disabled"
          :title="previousSessionHint.title"
          aria-label="Previous session"
          :aria-keyshortcuts="`${modKey}+[`"
          @mousedown.prevent
          @click="commandStore.runCommand('nav-prev-session')"
        >
          <kbd>[</kbd>
        </button>
        <button
          type="button"
          class="shortcut-key"
          data-testid="status-hint-next-session"
          :disabled="nextSessionHint.disabled"
          :title="nextSessionHint.title"
          aria-label="Next session"
          :aria-keyshortcuts="`${modKey}+]`"
          @mousedown.prevent
          @click="commandStore.runCommand('nav-next-session')"
        >
          <kbd>]</kbd>
        </button>
        Prev / next session
      </span>
      <span class="shortcut-separator">·</span>
      <button
        type="button"
        class="shortcut-hint"
        data-testid="status-hint-sidebar"
        :disabled="sidebarHint.disabled"
        :title="sidebarHint.title"
        :aria-label="sidebarHint.label || 'Sidebar'"
        :aria-keyshortcuts="`${modKey}+B`"
        @mousedown.prevent
        @click="commandStore.runCommand('toggle-sidebar')"
      >
        <kbd>{{ mod }} B</kbd> Sidebar
      </button>
      <span class="shortcut-separator">·</span>
      <button
        type="button"
        class="shortcut-hint"
        data-testid="status-hint-interrupt"
        :disabled="interruptHint.disabled"
        :title="interruptHint.title"
        aria-label="Interrupt"
        aria-keyshortcuts="Escape"
        @mousedown.prevent
        @click="commandStore.runCommand('interrupt-session')"
      >
        <kbd>Esc</kbd> Interrupt
      </button>
      <template v-if="appShell.config.terminalEnabled">
        <span class="shortcut-separator">·</span>
        <button
          type="button"
          class="shortcut-hint"
          data-testid="status-hint-terminal"
          :disabled="terminalHint.disabled"
          :title="terminalHint.title"
          :aria-label="terminalHint.label || 'Terminal'"
          :aria-keyshortcuts="`${modKey}+J`"
          @mousedown.prevent
          @click="commandStore.runCommand('toggle-terminal')"
        >
          <kbd>{{ mod }} J</kbd> Terminal
        </button>
      </template>
    </div>

    <div class="status-bar__end">
      <!-- Work left running in the background, in every session. -->
      <RunningWorkCounter />

      <!-- A harness's usage limit, only while it's close or used up. -->
      <UsageLimitChip />

      <!-- On the right, under the corner a notice card settles from. -->
      <NoticeChips />

      <!-- Session status lives on the session's row in the sidebar, not here. -->
      <div
        v-if="activeSession"
        class="status-bar__right"
        data-testid="status-bar-session"
      >
        <span class="model-badge">{{ modelBadge }}</span>

        <span class="status-separator">|</span>

        <span class="token-count">{{ tokenCount }}</span>
      </div>
    </div>
  </footer>
</template>

<style scoped>
.status-bar {
  display: flex;
  align-items: center;
  gap: 10px;
  height: 28px;
  min-height: 28px;
  padding: 0 12px 2px;
  background: var(--main-bg);
  font-size: 11px;
  color: var(--muted);
  user-select: none;
  z-index: 10;
}

.status-bar__left {
  display: flex;
  align-items: center;
  gap: 1px;
}

.status-bar__end {
  display: flex;
  min-width: 0;
  margin-left: auto;
  align-items: center;
  gap: 10px;
}

.status-bar__right {
  display: flex;
  align-items: center;
  gap: 8px;
  white-space: nowrap;
}

.shortcut-hint {
  display: flex;
  align-items: center;
  gap: 4px;
  padding: 2px 5px;
  color: var(--muted);
}

/* Quiet at rest like the plain hints; the theme's hover and pressed tints only when there's something to press. */
button.shortcut-hint,
.shortcut-key {
  margin: 0;
  border: 0;
  border-radius: var(--radius-btn);
  background: transparent;
  font: inherit;
  cursor: pointer;
  transition: background-color var(--transition), color var(--transition);
}

/* The app's focus ring (main.css) is faint on the bar, so a focused hint takes the hover tint as well. */
button.shortcut-hint:hover:not(:disabled),
button.shortcut-hint:focus-visible {
  background-color: color-mix(in srgb, var(--text) 6%, transparent);
  color: var(--text);
}

button.shortcut-hint:active:not(:disabled) {
  background-color: color-mix(in srgb, var(--text) 10%, transparent);
}

button.shortcut-hint:disabled,
.shortcut-key:disabled {
  opacity: 0.45;
  cursor: default;
}

/* Prev / next: one hint, with [ and ] as their own small buttons. */
.shortcut-hint--pair {
  gap: 2px;
}

.shortcut-key {
  display: flex;
  padding: 1px;
  color: inherit;
}

/* [ and ] are too small for a tint around them to show, so the key itself lights up. */
.shortcut-key:hover:not(:disabled) kbd,
.shortcut-key:focus-visible kbd {
  border-color: var(--muted);
  background: color-mix(in srgb, var(--text) 14%, transparent);
}

.shortcut-key:active:not(:disabled) kbd {
  background: color-mix(in srgb, var(--text) 20%, transparent);
}

.shortcut-hint--pair > kbd + .shortcut-key {
  margin-left: 1px;
}

.shortcut-hint--pair > .shortcut-key:last-of-type {
  margin-right: 2px;
}

.shortcut-hint kbd {
  display: inline-block;
  padding: 2px 4px;
  font-family: inherit;
  font-size: 10px;
  font-weight: 500;
  line-height: 1;
  color: var(--text);
  background: color-mix(in srgb, var(--text) 6%, transparent);
  border: 1px solid var(--border);
  border-radius: calc(var(--radius-btn) - 3px);
}

.shortcut-separator {
  color: var(--border);
}

.terminal-owner {
  padding: 2px 5px;
  color: var(--accent);
  font-weight: 500;
}

.status-separator {
  color: var(--border);
}

.model-badge {
  font-size: 11px;
  font-weight: 500;
  color: var(--muted);
  font-family: ui-monospace, SFMono-Regular, "SF Mono", Menlo, Consolas, "Liberation Mono", monospace;
}

.token-count {
  font-size: 11px;
  color: var(--muted);
  font-variant-numeric: tabular-nums;
}

/* Mobile: hide keyboard shortcuts on small screens */
@media (max-width: 768px) {
  .status-bar__left {
    display: none;
  }
}
</style>
