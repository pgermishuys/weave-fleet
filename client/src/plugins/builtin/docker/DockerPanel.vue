<script setup lang="ts">
import { computed, shallowRef } from "vue";
import { Boxes } from "lucide-vue-next";
import type { PluginConnectionStatus } from "@/plugins/types";

interface DockerContainerItem {
  id: string;
  name: string;
  image: string;
  status: string;
  statusColor: string;
}

const connectionStatus = shallowRef<PluginConnectionStatus>("connected");

const containers = Object.freeze<readonly DockerContainerItem[]>([
  {
    id: "fleet-api",
    name: "fleet-api",
    image: "weave/fleet-api:latest",
    status: "Running · 2h ago",
    statusColor: "#22c55e",
  },
  {
    id: "fleet-worker",
    name: "fleet-worker",
    image: "weave/fleet-worker:latest",
    status: "Restarting · 4m ago",
    statusColor: "#f59e0b",
  },
  {
    id: "redis",
    name: "redis-cache",
    image: "redis:7-alpine",
    status: "Running · 1d ago",
    statusColor: "#22c55e",
  },
]);

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
  <section class="simple-panel" aria-label="Docker panel">
    <header class="simple-panel-header">
      <div class="simple-panel-title-row">
        <Boxes :size="16" aria-hidden="true" />
        <h2 class="simple-panel-title">
          Docker
        </h2>
      </div>

      <span :class="connectionClassName">
        {{ connectionLabel }}
      </span>
    </header>

    <div class="simple-panel-content">
      <p class="simple-panel-copy">
        {{ containers.length }} local containers available.
      </p>

      <div class="container-list">
        <article v-for="container in containers" :key="container.id" class="container-item">
          <span class="container-dot" :style="{ backgroundColor: container.statusColor }" aria-hidden="true" />

          <div class="container-copy">
            <p class="container-name">
              {{ container.name }}
            </p>
            <p class="container-meta">
              {{ container.image }} · {{ container.status }}
            </p>
          </div>
        </article>
      </div>
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
  flex: 1;
  min-height: 0;
  padding: 14px;
  overflow-y: auto;
}

.simple-panel-copy {
  margin: 0 0 10px;
  font-size: 12px;
  color: var(--muted);
}

.container-list {
  display: flex;
  flex-direction: column;
}

.container-item {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 6px 0;
  font-size: 12px;
}

.container-dot {
  width: 6px;
  height: 6px;
  border-radius: 50%;
  flex-shrink: 0;
}

.container-copy {
  min-width: 0;
}

.container-name,
.container-meta {
  margin: 0;
}

.container-name {
  font-weight: 600;
  color: var(--text);
}

.container-meta {
  margin-top: 2px;
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
