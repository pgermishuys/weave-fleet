<script setup lang="ts">
import { computed } from "vue";
import { storeToRefs } from "pinia";
import { useAppShellStore } from "@/stores/app-shell";
import { useSessionsStore } from "@/stores/sessions";
import { useTerminalsStore } from "@/stores/terminals";

const sessionsStore = useSessionsStore();
const { sessions, activeSessionId } = storeToRefs(sessionsStore);
const appShell = useAppShellStore();
const { focused: terminalFocused } = storeToRefs(useTerminalsStore());

const isMac = navigator.platform.toUpperCase().indexOf("MAC") >= 0;
const mod = isMac ? "⌘" : "Ctrl";

const activeSession = computed(() =>
  sessions.value.find((session) => session.session.id === activeSessionId.value) ?? null,
);

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
      <span class="shortcut-hint">
        <kbd>{{ mod }} J</kbd> Hide terminal
      </span>
      <span class="shortcut-separator">·</span>
      <span class="shortcut-hint">
        <kbd>{{ isMac ? "⌘ C" : "Ctrl Shift C" }}</kbd> Copy
      </span>
    </div>
    <div
      v-else
      class="status-bar__left"
    >
      <span class="shortcut-hint">
        <kbd>{{ mod }} K</kbd> Command Palette
      </span>
      <span class="shortcut-separator">·</span>
      <span class="shortcut-hint">
        <kbd>{{ mod }} [ ]</kbd> Prev / Next Session
      </span>
      <span class="shortcut-separator">·</span>
      <span class="shortcut-hint">
        <kbd>{{ mod }} B</kbd> Sidebar
      </span>
      <span class="shortcut-separator">·</span>
      <span class="shortcut-hint">
        <kbd>Esc</kbd> Cancel
      </span>
      <template v-if="appShell.config.terminalEnabled">
        <span class="shortcut-separator">·</span>
        <span class="shortcut-hint">
          <kbd>{{ mod }} J</kbd> Terminal
        </span>
      </template>
    </div>

    <!-- Session status lives on the session's row in the sidebar, not here. -->
    <div class="status-bar__right">
      <span class="model-badge">{{ modelBadge }}</span>

      <span class="status-separator">|</span>

      <span class="token-count">{{ tokenCount }}</span>
    </div>
  </footer>
</template>

<style scoped>
.status-bar {
  display: flex;
  align-items: center;
  justify-content: space-between;
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
  gap: 6px;
}

.status-bar__right {
  display: flex;
  align-items: center;
  gap: 8px;
}

.shortcut-hint {
  display: flex;
  align-items: center;
  gap: 4px;
  color: var(--muted);
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
