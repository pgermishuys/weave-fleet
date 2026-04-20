<script setup lang="ts">
import { computed, shallowRef } from "vue";
import { useRouter } from "@tanstack/vue-router";
import { BadgeCheck, CircleAlert, PlugZap, Puzzle, Settings2 } from "lucide-vue-next";
import { usePluginRuntime } from "@/plugins/composable";
import type { PluginConnectionStatus } from "@/plugins/types";

interface InstalledPlugin {
  id: string;
  name: string;
   status: PluginConnectionStatus;
   configureLabel: string;
}

interface AvailablePlugin {
  id: string;
  name: string;
  description: string;
}

const availablePluginCatalog = [
  {
    id: "linear",
    name: "Linear",
    description: "Sync issues, projects, and roadmap context into Fleet.",
  },
  {
    id: "docker",
    name: "Docker",
    description: "Inspect local containers and connect runtime signals to sessions.",
  },
  {
    id: "notion",
    name: "Notion",
    description: "Pull workspace docs and planning notes into execution context.",
  },
  {
    id: "jira",
    name: "Jira",
    description: "Bring sprint tickets and delivery status into the board.",
  },
] as const satisfies readonly AvailablePlugin[];

const router = useRouter();
const pluginRuntime = usePluginRuntime();
const installMessage = shallowRef<string | null>(null);

const installedPlugins = computed<readonly InstalledPlugin[]>(() => {
  return pluginRuntime.manifests.value
    .filter((manifest) => manifest.descriptor.id !== "marketplace")
    .map((manifest) => ({
      id: manifest.descriptor.id,
      name: manifest.descriptor.displayName,
      status: pluginRuntime.getStatus(manifest.descriptor.id)?.status ?? "disconnected",
      configureLabel: "Configure",
    }));
});

const installedPluginIds = computed(() => new Set(installedPlugins.value.map((plugin) => plugin.id)));

const installedPluginRows = computed(() =>
  installedPlugins.value.map((plugin) => ({
    ...plugin,
    statusLabel: getStatusLabel(plugin.status),
    statusClass: `is-${plugin.status}`,
    statusIcon: getStatusIcon(plugin.status),
  })),
);

const availablePlugins = computed(() =>
  availablePluginCatalog.filter((plugin) => !installedPluginIds.value.has(plugin.id)),
);

function getStatusLabel(status: PluginConnectionStatus): string {
  switch (status) {
    case "connected":
      return "Connected";
    case "disconnected":
      return "Disconnected";
    case "error":
      return "Needs attention";
  }
}

function getStatusIcon(status: PluginConnectionStatus) {
  switch (status) {
    case "connected":
      return BadgeCheck;
    case "disconnected":
      return PlugZap;
    case "error":
      return CircleAlert;
  }
}
function handleConfigure(pluginId: string): void {
  void router.navigate({
    to: "/settings/plugins/$pluginId",
    params: { pluginId },
  });
}

function handleInstall(pluginId: string): void {
  const plugin = availablePlugins.value.find((candidate) => candidate.id === pluginId);
  installMessage.value = plugin
    ? `${plugin.name} install flow is not available in this build yet.`
    : "Plugin install flow is not available in this build yet.";
}
</script>

<template>
  <section class="marketplace-panel" aria-label="Marketplace panel">
    <p class="mp-section-label">
      Installed
    </p>

    <div class="mp-installed-list">
      <article
        v-for="plugin in installedPluginRows"
        :key="plugin.id"
        class="mp-installed-item"
      >
        <div class="mp-plugin-icon" aria-hidden="true">
          <Puzzle :size="16" />
        </div>

        <div class="mp-installed-copy">
          <p class="mp-plugin-name">
            {{ plugin.name }}
          </p>
          <p class="mp-plugin-status" :class="plugin.statusClass">
            <component :is="plugin.statusIcon" :size="12" aria-hidden="true" />
            <span>{{ plugin.statusLabel }}</span>
          </p>
        </div>

        <button
          type="button"
          class="mp-link-button"
          @click="handleConfigure(plugin.id)"
        >
          <Settings2 :size="14" aria-hidden="true" />
          <span>{{ plugin.configureLabel }}</span>
        </button>
      </article>
    </div>

    <p class="mp-section-label">
      Available
    </p>

    <div class="mp-grid">
      <article
        v-for="plugin in availablePlugins"
        :key="plugin.id"
        class="mp-card"
      >
        <div class="mp-card-header">
          <div class="mp-plugin-icon" aria-hidden="true">
            <Puzzle :size="16" />
          </div>

          <p class="mp-plugin-name">
            {{ plugin.name }}
          </p>
        </div>

        <p class="mp-card-description">
          {{ plugin.description }}
        </p>

        <button
          type="button"
          class="mp-install-button"
          @click="handleInstall(plugin.id)"
        >
          Install
        </button>
      </article>
    </div>

    <p v-if="installMessage" class="mp-install-message" aria-live="polite">
      {{ installMessage }}
    </p>
  </section>
</template>

<style scoped>
.marketplace-panel {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-height: 0;
  overflow-y: auto;
}

.mp-section-label {
  padding: 12px 12px 6px;
  font-size: 11px;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  color: var(--muted);
}

.mp-installed-list {
  border-top: 1px solid var(--border);
}

.mp-installed-item {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px 12px;
  border-bottom: 1px solid var(--border);
}

.mp-installed-copy {
  display: flex;
  flex: 1;
  min-width: 0;
  flex-direction: column;
  gap: 3px;
}

.mp-grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 8px;
  padding: 8px 12px;
}

.mp-card {
  display: flex;
  flex-direction: column;
  gap: 10px;
  background: var(--card-bg);
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  padding: 10px;
}

.mp-card-header {
  display: flex;
  align-items: center;
  gap: 8px;
}

.mp-plugin-icon {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 28px;
  height: 28px;
  border-radius: 8px;
  color: var(--text);
  background: rgba(255, 255, 255, 0.06);
  border: 1px solid var(--border);
  flex-shrink: 0;
}

.mp-plugin-name {
  margin: 0;
  font-size: 13px;
  font-weight: 600;
  color: var(--text);
}

.mp-plugin-status {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  margin: 0;
  font-size: 12px;
  color: var(--muted);
}

.mp-plugin-status.is-connected {
  color: #22c55e;
}

.mp-plugin-status.is-disconnected {
  color: var(--muted);
}

.mp-plugin-status.is-error {
  color: var(--error);
}

.mp-card-description {
  margin: 0;
  min-height: 54px;
  font-size: 12px;
  line-height: 1.5;
  color: var(--muted);
}

.mp-install-message {
  margin: 0;
  padding: 0 12px 12px;
  font-size: 12px;
  color: var(--muted);
}

.mp-link-button,
.mp-install-button {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: 6px;
  border-radius: var(--radius-btn);
  border: 1px solid var(--border);
  background: transparent;
  color: var(--text);
  cursor: pointer;
  transition: background 0.15s ease, border-color 0.15s ease, color 0.15s ease;
}

.mp-link-button:hover,
.mp-install-button:hover {
  background: rgba(255, 255, 255, 0.04);
  border-color: rgba(255, 255, 255, 0.12);
}

.mp-link-button {
  padding: 7px 10px;
  font-size: 12px;
  white-space: nowrap;
}

.mp-install-button {
  width: 100%;
  padding: 8px 10px;
  font-size: 12px;
  font-weight: 600;
}
</style>
