<script setup lang="ts">
import type { Component } from "vue";
import { computed } from "vue";
import { useRouter, useRouterState } from "@tanstack/vue-router";
import { ArrowLeft, AlertCircle, PlugZap, Settings2 } from "lucide-vue-next";
import { usePluginRuntime } from "@/plugins/composable";
import { getConfigPage } from "@/plugins/slots";

const buttonSecondaryClass = "inline-flex items-center justify-center gap-2 rounded-btn border border-border bg-main-bg px-3 py-2 text-sm font-medium text-text transition-colors hover:border-accent/50 disabled:cursor-not-allowed disabled:opacity-60";

const router = useRouter();
const pluginRuntime = usePluginRuntime();

const pluginId = useRouterState({
  select: (state) => {
    for (const match of [...state.matches].reverse()) {
      const params = match.params as Record<string, unknown>;
      if (typeof params.pluginId === "string" && params.pluginId.length > 0) {
        return params.pluginId;
      }
    }

    return undefined;
  },
});

const manifest = computed(() => {
  if (!pluginId.value) {
    return undefined;
  }

  return pluginRuntime.manifests.value.find((candidate) => candidate.descriptor.id === pluginId.value);
});

const configPage = computed(() => {
  if (!pluginId.value) {
    return undefined;
  }

  return getConfigPage(pluginId.value, pluginRuntime.manifests.value);
});

const descriptor = computed(() => manifest.value?.descriptor);
const pluginStatus = computed(() => (pluginId.value ? pluginRuntime.getStatus(pluginId.value) : undefined));

const pluginIcon = computed<Component>(() => {
  return configPage.value?.icon ?? Settings2;
});

const statusLabel = computed(() => {
  if (pluginRuntime.isLoading.value && !pluginStatus.value) {
    return "Checking";
  }

  switch (pluginStatus.value?.status) {
    case "connected":
      return "Connected";
    case "error":
      return "Error";
    case "disconnected":
      return "Disconnected";
    default:
      return "Disconnected";
  }
});

const statusTone = computed(() => {
  if (pluginRuntime.isLoading.value && !pluginStatus.value) return "checking";
  return pluginStatus.value?.status ?? "disconnected";
});

const fallbackState = computed<{
  title: string;
  message: string;
} | null>(() => {
  if (!pluginId.value) {
    return {
      title: "Plugin configuration not found",
      message: "The current route does not include a plugin identifier for a dedicated settings page.",
    };
  }

  if (!manifest.value) {
    return {
      title: "Plugin configuration not found",
      message: `No registered plugin matches \"${pluginId.value}\".`,
    };
  }

  if (!configPage.value) {
    return {
      title: "Configuration page unavailable",
      message: `${manifest.value.descriptor.displayName} does not provide a dedicated configuration page.`,
    };
  }

  return null;
});

function handleBack(): void {
  void router.navigate({ to: "/settings" });
}
</script>

<template>
  <section class="plugin-page">
    <header class="plugin-page__head">
      <button
        type="button"
        class="plugin-page__back"
        @click="handleBack"
      >
        <ArrowLeft
          :size="14"
          aria-hidden="true"
        />
        <span>Settings</span>
      </button>

      <div class="plugin-page__title-row">
        <span class="plugin-page__icon">
          <component
            :is="pluginIcon"
            :size="18"
            aria-hidden="true"
          />
        </span>
        <h1 class="plugin-page__title">
          {{ descriptor?.displayName ?? fallbackState?.title }}
        </h1>
        <span
          class="plugin-page__status"
          :class="`plugin-page__status--${statusTone}`"
        >
          <span
            class="plugin-page__status-dot"
            aria-hidden="true"
          />
          {{ statusLabel }}
        </span>
      </div>

      <p class="plugin-page__description">
        {{ configPage ? configPage.title : fallbackState?.message }}
      </p>
    </header>

    <section
      v-if="fallbackState"
      class="rounded-card border border-dashed border-border bg-card-bg p-10 text-center shadow-sm"
      aria-live="polite"
    >
      <div class="mx-auto flex max-w-xl flex-col items-center gap-4">
        <div class="rounded-full border border-border bg-main-bg p-3 text-muted">
          <AlertCircle
            :size="24"
            aria-hidden="true"
          />
        </div>
        <div class="space-y-2">
          <h2 class="text-xl font-semibold tracking-tight text-text">
            {{ fallbackState.title }}
          </h2>
          <p class="text-sm text-muted">
            {{ fallbackState.message }}
          </p>
        </div>
        <button
          type="button"
          :class="buttonSecondaryClass"
          @click="handleBack"
        >
          <PlugZap
            :size="16"
            aria-hidden="true"
          />
          <span>Return to settings</span>
        </button>
      </div>
    </section>

    <section
      v-else-if="configPage && descriptor"
      class="plugin-page__panel"
    >
      <component
        :is="configPage.component"
        :descriptor="descriptor"
        :status="pluginStatus"
      />
    </section>
  </section>
</template>

<style scoped>
.plugin-page {
  display: flex;
  flex-direction: column;
  gap: 20px;
  width: 100%;
  max-width: 880px;
}

.plugin-page__head {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.plugin-page__back {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  width: fit-content;
  margin: 0 0 6px -6px;
  padding: 3px 6px;
  border: 0;
  border-radius: var(--radius-btn);
  background: transparent;
  color: var(--muted);
  font-size: 12.5px;
  cursor: pointer;
  transition: background-color var(--transition), color var(--transition);
}

.plugin-page__back:hover {
  background: color-mix(in srgb, var(--text) 6%, transparent);
  color: var(--text);
}

.plugin-page__title-row {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 10px;
}

.plugin-page__icon {
  display: grid;
  place-items: center;
  width: 32px;
  height: 32px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--card-bg);
  color: var(--text);
}

.plugin-page__title {
  margin: 0;
  color: var(--text);
  font-size: 22px;
  font-weight: 600;
  letter-spacing: -0.01em;
}

.plugin-page__status {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  color: var(--muted);
  font-size: 12px;
}

.plugin-page__status-dot {
  width: 7px;
  height: 7px;
  border-radius: 50%;
  background: var(--muted);
}

.plugin-page__status--connected .plugin-page__status-dot {
  background: var(--running);
}

.plugin-page__status--error {
  color: var(--error);
}

.plugin-page__status--error .plugin-page__status-dot {
  background: var(--error);
}

.plugin-page__description {
  margin: 0;
  color: var(--muted);
  font-size: 13px;
}

.plugin-page__panel {
  padding: 20px;
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  background: var(--card-bg);
}
</style>
