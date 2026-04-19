<script setup lang="ts">
import { computed } from "vue";
import { PlugZap } from "lucide-vue-next";
import { Button } from "@/components/ui/button";
import { usePluginRuntime } from "@/plugins/composable";

const pluginRuntime = usePluginRuntime();

const pluginStatus = computed(() => pluginRuntime.getStatus("linear")?.status ?? "disconnected");

const connectionLabel = computed(() => {
  switch (pluginStatus.value) {
    case "error":
      return "Error";
    case "connected":
      return "Connected";
    case "disconnected":
    default:
      return "Not connected";
  }
});

const connectionClassName = computed(() => {
  switch (pluginStatus.value) {
    case "error":
      return "error";
    case "connected":
      return "connected";
    case "disconnected":
    default:
      return "disconnected";
  }
});
</script>

<template>
  <section class="linear-panel" aria-label="Linear panel">
    <header class="linear-panel-header">
      <p class="linear-panel-eyebrow">
        Plugin
      </p>

      <div class="linear-panel-heading-row">
        <div class="linear-panel-heading-copy">
          <h2 class="linear-panel-title">
            Linear
          </h2>
          <p class="linear-panel-description">
            Track active work across your team.
          </p>
        </div>

        <span class="linear-panel-connection" :class="connectionClassName">
          {{ connectionLabel }}
        </span>
      </div>
    </header>

    <div class="linear-panel-empty-state">
      <div class="linear-panel-empty-icon" aria-hidden="true">
        <PlugZap :size="20" />
      </div>
      <p class="linear-panel-empty-title">
        Connect Linear to get started
      </p>
      <p class="linear-panel-empty-description">
        Linear integration is not set up yet. Connect your workspace to view issues and triage work from this panel.
      </p>
      <Button type="button" class="linear-panel-empty-action" disabled>
        Connect
      </Button>
    </div>
  </section>
</template>

<style scoped>
.linear-panel {
  display: flex;
  flex: 1;
  min-height: 0;
  flex-direction: column;
}

.linear-panel-header {
  display: flex;
  flex-direction: column;
  gap: 12px;
  padding: 20px 16px 12px;
  border-bottom: 1px solid var(--border);
}

.linear-panel-eyebrow {
  margin: 0;
  font-size: 11px;
  font-weight: 600;
  letter-spacing: 0.05em;
  text-transform: uppercase;
  color: var(--muted);
}

.linear-panel-heading-row {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
}

.linear-panel-heading-copy {
  min-width: 0;
}

.linear-panel-title {
  margin: 0;
  font-size: 18px;
  font-weight: 600;
  color: var(--text);
}

.linear-panel-description {
  margin: 4px 0 0;
  font-size: 13px;
  line-height: 1.5;
  color: var(--muted);
}

.linear-panel-connection {
  border-radius: 999px;
  padding: 5px 8px;
  font-size: 11px;
  font-weight: 600;
  white-space: nowrap;
}

.linear-panel-connection.connected {
  background: rgba(34, 197, 94, 0.15);
  color: #22c55e;
}

.linear-panel-connection.disconnected {
  background: rgba(113, 113, 122, 0.15);
  color: var(--muted);
}

.linear-panel-empty-state {
  display: flex;
  flex: 1;
  flex-direction: column;
  justify-content: center;
  align-items: flex-start;
  gap: 10px;
  padding: 24px 16px;
}

.linear-panel-empty-icon {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 40px;
  height: 40px;
  border: 1px solid var(--border);
  border-radius: 12px;
  background: rgba(255, 255, 255, 0.03);
  color: var(--muted);
}

.linear-panel-empty-title {
  margin: 0;
  font-size: 14px;
  font-weight: 600;
  color: var(--text);
}

.linear-panel-empty-description {
  margin: 0;
  font-size: 13px;
  line-height: 1.5;
  color: var(--muted);
}

.linear-panel-empty-action {
  margin-top: 4px;
}

.linear-panel-connection.error {
  background: rgba(239, 68, 68, 0.15);
  color: var(--error);
}
</style>
