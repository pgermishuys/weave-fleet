<script setup lang="ts">
import { computed, shallowRef } from "vue";
import { Slack } from "lucide-vue-next";
import type { PluginConnectionStatus } from "@/plugins/types";

const connectionStatus = shallowRef<PluginConnectionStatus>("disconnected");

const connectionLabel = computed(() => {
  switch (connectionStatus.value) {
    case "disconnected":
      return "Disconnected";
    case "error":
      return "Error";
    case "connected":
    default:
      return "Connected";
  }
});

const connectionClassName = computed(() => {
  switch (connectionStatus.value) {
    case "disconnected":
      return "disconnected-pill";
    case "error":
      return "error-pill";
    case "connected":
    default:
      return "connected-pill";
  }
});
</script>

<template>
  <section
    class="simple-panel"
    aria-label="Slack panel"
  >
    <header class="simple-panel-header">
      <div class="simple-panel-title-row">
        <Slack
          :size="16"
          aria-hidden="true"
        />
        <h2 class="simple-panel-title">
          Slack
        </h2>
      </div>

      <span :class="connectionClassName">
        {{ connectionLabel }}
      </span>
    </header>

    <div class="simple-panel-content">
      <p class="slack-heading">
        Slack is not connected yet.
      </p>
      <p class="slack-copy">
        Connect a workspace to view channels, notifications, and message context from this panel.
      </p>
    </div>
  </section>
</template>

<style scoped>
.simple-panel {
  display: flex;
  flex: 1;
  min-height: 0;
  flex-direction: column;
  background: var(--panel-bg);
}

.simple-panel-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 14px;
  border-bottom: 1px solid var(--border);
}

.simple-panel-title-row {
  display: flex;
  align-items: center;
  gap: 8px;
}

.simple-panel-title {
  margin: 0;
  font-size: 16px;
  font-weight: 700;
  color: var(--text);
}

.simple-panel-content {
  padding: 14px;
}

.slack-heading,
.slack-copy {
  margin: 0;
}

.slack-heading {
  font-size: 12px;
  font-weight: 600;
  color: var(--text);
}

.slack-copy {
  margin-top: 8px;
  font-size: 11px;
  line-height: 1.5;
  color: var(--muted);
}

.connected-pill,
.disconnected-pill,
.error-pill {
  display: inline-flex;
  align-items: center;
  border-radius: 999px;
  padding: 3px 8px;
  font-size: 10px;
  font-weight: 600;
}

.connected-pill {
  background: rgba(34, 197, 94, 0.15);
  color: #22c55e;
}

.disconnected-pill {
  background: rgba(113, 113, 122, 0.15);
  color: var(--muted);
}

.error-pill {
  background: rgba(239, 68, 68, 0.15);
  color: var(--error);
}
</style>
