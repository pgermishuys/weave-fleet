<script setup lang="ts">
import { computed, shallowRef } from "vue";
import { BadgeAlert } from "lucide-vue-next";
import type { PluginConnectionStatus } from "@/plugins/types";

interface SentryErrorItem {
  id: string;
  title: string;
  level: "error" | "warning";
  project: string;
  occurrences: number;
}

const connectionStatus = shallowRef<PluginConnectionStatus>("error");

const errors = Object.freeze<readonly SentryErrorItem[]>([
  {
    id: "evt-1001",
    title: "TypeError: Cannot read properties of undefined",
    level: "error",
    project: "client",
    occurrences: 18,
  },
  {
    id: "evt-1002",
    title: "FetchError: plugin catalog request timed out",
    level: "warning",
    project: "fleet-api",
    occurrences: 6,
  },
  {
    id: "evt-1003",
    title: "ReferenceError: sidebarStore is not defined",
    level: "error",
    project: "workspace-shell",
    occurrences: 3,
  },
]);

const errorCount = computed(() => errors.length);

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
    aria-label="Sentry panel"
  >
    <header class="simple-panel-header">
      <div class="simple-panel-title-row">
        <BadgeAlert
          :size="16"
          aria-hidden="true"
        />
        <h2 class="simple-panel-title">
          Sentry
        </h2>
      </div>

      <div class="simple-panel-status-row">
        <span
          class="badge-count"
          aria-label="Open Sentry issues"
        >
          {{ errorCount }}
        </span>
        <span :class="connectionClassName">
          {{ connectionLabel }}
        </span>
      </div>
    </header>

    <div class="simple-panel-content">
      <article
        v-for="errorItem in errors"
        :key="errorItem.id"
        class="error-item"
      >
        <div class="error-item-header">
          <p class="error-title">
            {{ errorItem.title }}
          </p>
          <span class="error-level">
            {{ errorItem.level }}
          </span>
        </div>

        <p class="error-meta">
          {{ errorItem.project }} · {{ errorItem.occurrences }} events
        </p>
      </article>
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

.simple-panel-title-row,
.simple-panel-status-row,
.error-item-header {
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
  flex: 1;
  min-height: 0;
  padding: 14px;
  overflow-y: auto;
}

.error-item {
  padding: 8px 0;
  border-bottom: 1px solid var(--border);
}

.error-item:last-child {
  border-bottom: 0;
}

.error-title,
.error-meta {
  margin: 0;
}

.error-title {
  flex: 1;
  font-size: 12px;
  font-weight: 600;
  color: var(--text);
}

.error-meta,
.error-level {
  font-size: 11px;
  color: var(--muted);
}

.error-level {
  text-transform: capitalize;
}

.badge-count {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-width: 18px;
  height: 18px;
  border-radius: 999px;
  padding: 0 6px;
  background: rgba(239, 68, 68, 0.15);
  color: var(--error);
  font-size: 10px;
  font-weight: 700;
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
