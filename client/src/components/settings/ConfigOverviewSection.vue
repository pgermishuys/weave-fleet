<script setup lang="ts">
import { computed } from "vue";
import { AlertCircle, LoaderCircle } from "lucide-vue-next";
import { useConfig } from "@/composables/use-config";

const {
  config,
  error: configError,
  installedSkills,
  isLoading: isConfigLoading,
  paths,
  providers,
} = useConfig();

const configuredAgentCount = computed(() => Object.keys(config.value?.agents ?? {}).length);
const connectedProviderCount = computed(() => providers.value.filter((provider) => provider.connected).length);
const totalModelCount = computed(() => providers.value.reduce((count, provider) => count + provider.models.length, 0));
</script>

<template>
  <section class="rounded-card border border-border bg-card-bg p-6 shadow-sm">
    <div class="flex flex-col gap-1">
      <h2 class="text-lg font-semibold text-text">
        Configuration overview
      </h2>
      <p class="text-sm text-muted">
        Review the active user config, installed skills, and provider connectivity reported by the API.
      </p>
    </div>

    <div
      v-if="isConfigLoading"
      class="mt-5 flex items-center gap-2 text-sm text-muted"
    >
      <LoaderCircle
        :size="16"
        class="animate-spin"
        aria-hidden="true"
      />
      <span>Loading configuration…</span>
    </div>

    <div
      v-else-if="configError"
      class="mt-5 flex items-start gap-2 rounded-card border border-red-500/30 bg-red-500/10 px-3 py-2 text-sm text-red-200"
      role="alert"
    >
      <AlertCircle
        :size="16"
        class="mt-0.5 shrink-0"
        aria-hidden="true"
      />
      <span>{{ configError }}</span>
    </div>

    <div
      v-else
      class="mt-5 space-y-4"
    >
      <div class="grid gap-3 sm:grid-cols-2">
        <article class="rounded-card border border-border bg-main-bg p-4">
          <p class="text-xs font-medium uppercase tracking-wide text-muted">
            User config
          </p>
          <p class="mt-2 break-all text-sm text-text">
            {{ paths?.userConfig ?? "Unavailable" }}
          </p>
        </article>

        <article class="rounded-card border border-border bg-main-bg p-4">
          <p class="text-xs font-medium uppercase tracking-wide text-muted">
            Skills directory
          </p>
          <p class="mt-2 break-all text-sm text-text">
            {{ paths?.skillsDir ?? "Unavailable" }}
          </p>
        </article>

        <article class="rounded-card border border-border bg-main-bg p-4">
          <p class="text-xs font-medium uppercase tracking-wide text-muted">
            Configured agents
          </p>
          <p class="mt-2 text-2xl font-semibold text-text">
            {{ configuredAgentCount }}
          </p>
        </article>

        <article class="rounded-card border border-border bg-main-bg p-4">
          <p class="text-xs font-medium uppercase tracking-wide text-muted">
            Installed skills
          </p>
          <p class="mt-2 text-2xl font-semibold text-text">
            {{ installedSkills.length }}
          </p>
        </article>

        <article class="rounded-card border border-border bg-main-bg p-4">
          <p class="text-xs font-medium uppercase tracking-wide text-muted">
            Connected providers
          </p>
          <p class="mt-2 text-2xl font-semibold text-text">
            {{ connectedProviderCount }}
          </p>
        </article>

        <article class="rounded-card border border-border bg-main-bg p-4">
          <p class="text-xs font-medium uppercase tracking-wide text-muted">
            Available models
          </p>
          <p class="mt-2 text-2xl font-semibold text-text">
            {{ totalModelCount }}
          </p>
        </article>
      </div>

      <div class="rounded-card border border-border bg-main-bg p-4">
        <div class="flex flex-col gap-1">
          <h3 class="text-sm font-semibold text-text">
            Provider status
          </h3>
          <p class="text-xs text-muted">
            Shows connectivity and advertised models returned by <code>/api/config</code>.
          </p>
        </div>

        <div
          v-if="providers.length === 0"
          class="mt-4 rounded-card border border-dashed border-border p-4 text-sm text-muted"
        >
          No provider status reported.
        </div>

        <div
          v-else
          class="mt-4 grid gap-3"
        >
          <article
            v-for="provider in providers"
            :key="provider.id"
            class="rounded-card border border-border bg-card-bg p-4"
          >
            <div class="flex flex-wrap items-center justify-between gap-3">
              <div>
                <h4 class="text-sm font-semibold text-text">
                  {{ provider.name }}
                </h4>
                <p class="mt-1 text-xs text-muted">
                  {{ provider.authType ?? "No auth type reported" }} · {{ provider.models.length }} models
                </p>
              </div>

              <span
                :class="provider.connected
                  ? 'rounded-full border border-green-500/30 bg-green-500/10 px-2 py-1 text-[11px] font-medium text-green-300'
                  : 'rounded-full border border-border px-2 py-1 text-[11px] font-medium text-muted'"
              >
                {{ provider.connected ? "Connected" : "Disconnected" }}
              </span>
            </div>
          </article>
        </div>
      </div>
    </div>
  </section>
</template>
