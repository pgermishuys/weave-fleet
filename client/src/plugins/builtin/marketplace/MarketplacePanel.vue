<script setup lang="ts">
import { computed } from "vue";
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

const router = useRouter();
const pluginRuntime = usePluginRuntime();

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

const installedPluginRows = computed(() =>
  installedPlugins.value.map((plugin) => ({
    ...plugin,
    statusLabel: getStatusLabel(plugin.status),
    statusClass: `is-${plugin.status}`,
    statusIcon: getStatusIcon(plugin.status),
  })),
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
</script>

<template>
  <section
    class="marketplace-panel"
    aria-label="Plugins panel"
  >
    <p class="mp-section-label">
      Installed
    </p>

    <div class="mp-installed-list">
      <article
        v-for="plugin in installedPluginRows"
        :key="plugin.id"
        class="mp-installed-item"
      >
        <div
          class="mp-plugin-icon"
          aria-hidden="true"
        >
          <Puzzle :size="16" />
        </div>

        <div class="mp-installed-copy">
          <p class="mp-plugin-name">
            {{ plugin.name }}
          </p>
          <p
            class="mp-plugin-status"
            :class="plugin.statusClass"
          >
            <component
              :is="plugin.statusIcon"
              :size="12"
              aria-hidden="true"
            />
            <span>{{ plugin.statusLabel }}</span>
          </p>
        </div>

        <button
          type="button"
          class="mp-link-button"
          @click="handleConfigure(plugin.id)"
        >
          <Settings2
            :size="14"
            aria-hidden="true"
          />
          <span>{{ plugin.configureLabel }}</span>
        </button>
      </article>
    </div>
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
  font-size: 10px;
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
  font-size: 12px;
  font-weight: 600;
  color: var(--text);
}

.mp-plugin-status {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  margin: 0;
  font-size: 11px;
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

.mp-link-button {
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
  padding: 7px 10px;
  font-size: 11px;
  white-space: nowrap;
}

.mp-link-button:hover {
  background: rgba(255, 255, 255, 0.04);
  border-color: rgba(255, 255, 255, 0.12);
}
</style>
