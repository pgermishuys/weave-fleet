<script setup lang="ts">
import { computed } from "vue";
import { storeToRefs } from "pinia";
import { useSessionsStore } from "@/stores/sessions";

const sessionsStore = useSessionsStore();
const { sessions, activeSessionId } = storeToRefs(sessionsStore);

const isMac = navigator.platform.toUpperCase().indexOf("MAC") >= 0;
const mod = isMac ? "⌘" : "Ctrl";

const activeSession = computed(() =>
  sessions.value.find((session) => session.session.id === activeSessionId.value) ?? null,
);

const statusLabel = computed(() => {
  const status = activeSession.value?.sessionStatus;
  switch (status) {
    case "idle":
      return "IDLE";
    case "active":
      return "ACTIVE";
    case "completed":
      return "COMPLETED";
    case "stopped":
    case "disconnected":
      return "STOPPED";
    case "error":
      return "ERROR";
    case "waiting_input":
      return "WAITING";
    case "resuming":
      return "RESUMING";
    default:
      return "IDLE";
  }
});

const statusColor = computed(() => {
  const status = activeSession.value?.sessionStatus;
  switch (status) {
    case "idle":
      return "rgb(34, 197, 94)"; // green
    case "active":
      return "rgb(245, 158, 11)"; // amber
    case "completed":
      return "rgb(56, 189, 248)"; // sky
    case "error":
      return "rgb(239, 68, 68)"; // red
    case "waiting_input":
      return "rgb(168, 85, 247)"; // purple
    default:
      return "rgb(34, 197, 94)"; // green
  }
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
    <div class="status-bar__left">
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
    </div>

    <div class="status-bar__right">
      <div class="status-indicator">
        <span
          class="status-dot"
          :style="{ backgroundColor: statusColor }"
        />
        <span class="status-label">{{ statusLabel }}</span>
      </div>

      <span class="status-separator">|</span>

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
  padding: 0 12px;
  background: var(--panel-bg);
  border-top: 1px solid var(--border);
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
  background: rgba(255, 255, 255, 0.05);
  border: 1px solid var(--border);
  border-radius: 0;
}

.shortcut-separator {
  color: var(--border);
}

.status-indicator {
  display: flex;
  align-items: center;
  gap: 6px;
}

.status-dot {
  width: 6px;
  height: 6px;
  border-radius: 50%;
}

.status-label {
  font-size: 11px;
  font-weight: 500;
  color: var(--text);
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
