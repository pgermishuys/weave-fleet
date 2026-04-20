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

const statusClassName = computed(() => {
  const status = pluginRuntime.isLoading.value && !pluginStatus.value
    ? "checking"
    : pluginStatus.value?.status ?? "disconnected";

  switch (status) {
    case "connected":
      return "rounded-full border border-green-500/30 bg-green-500/10 px-2 py-1 text-[11px] font-medium text-green-300";
    case "error":
      return "rounded-full border border-red-500/30 bg-red-500/10 px-2 py-1 text-[11px] font-medium text-red-200";
    case "checking":
      return "rounded-full border border-border bg-main-bg px-2 py-1 text-[11px] font-medium text-muted";
    default:
      return "rounded-full border border-border bg-main-bg px-2 py-1 text-[11px] font-medium text-muted";
  }
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
  <section class="grid gap-6">
    <div class="rounded-card border border-border bg-card-bg p-6 shadow-sm">
      <div class="flex flex-col gap-4">
        <button
          type="button"
          :class="buttonSecondaryClass"
          class="w-fit"
          @click="handleBack"
        >
          <ArrowLeft
            :size="16"
            aria-hidden="true"
          />
          <span>Back to settings</span>
        </button>

        <div class="flex flex-col gap-4 md:flex-row md:items-start md:justify-between">
          <div class="flex items-start gap-3">
            <div class="rounded-btn border border-border bg-main-bg p-2 text-text">
              <component
                :is="pluginIcon"
                :size="18"
                aria-hidden="true"
              />
            </div>

            <div class="space-y-2">
              <div class="flex flex-wrap items-center gap-2">
                <h1 class="text-2xl font-semibold tracking-tight text-text">
                  {{ descriptor?.displayName ?? fallbackState?.title }}
                </h1>
                <span :class="statusClassName">
                  {{ statusLabel }}
                </span>
              </div>

              <p
                v-if="configPage"
                class="max-w-3xl text-sm text-muted"
              >
                {{ configPage.title }}
              </p>

              <p
                v-else
                class="max-w-3xl text-sm text-muted"
              >
                {{ fallbackState?.message }}
              </p>
            </div>
          </div>
        </div>
      </div>
    </div>

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
      class="rounded-card border border-border bg-card-bg p-6 shadow-sm"
    >
      <component
        :is="configPage.component"
        :descriptor="descriptor"
        :status="pluginStatus"
      />
    </section>
  </section>
</template>
