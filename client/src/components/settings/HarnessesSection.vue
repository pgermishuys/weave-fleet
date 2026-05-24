<script setup lang="ts">
import type { Component } from "vue";
import { computed, onMounted, shallowRef } from "vue";
import {
  AlertTriangle,
  Cable,
  CheckCircle2,
  CircleDashed,
  Cpu,
  Hexagon,
  Infinity,
  Settings2,
  Star,
  TerminalSquare,
} from "lucide-vue-next";
import { useHarnesses } from "@/composables/use-harnesses";
import { usePreferencesStore } from "@/stores/preferences";
import type { HarnessInfo } from "@/lib/api-types";
import NuCodeSettingsPanel from "@/components/settings/NuCodeSettingsPanel.vue";

type HarnessStatus = "ready" | "missing-credentials" | "not-configured" | "disabled";

interface HarnessDisplayMetadata {
  eyebrow: string;
  description: string;
  icon: Component;
}

interface HarnessCard {
  id: string;
  name: string;
  eyebrow: string;
  description: string;
  summary: string;
  icon: Component;
  status: HarnessStatus;
  enabled: boolean;
  configurable: boolean;
  canToggle: boolean;
  canDefault: boolean;
}

const DEFAULT_HARNESS_TYPE = "opencode";

const harnessDisplayMetadata: Record<string, HarnessDisplayMetadata> = {
  nucode: {
    eyebrow: "Local harness",
    description: "In-process AI coding harness for sessions that run inside Weave.",
    icon: Cpu,
  },
  opencode: {
    eyebrow: "CLI harness",
    description: "Harness for sessions backed by the OpenCode command-line runtime.",
    icon: TerminalSquare,
  },
  "claude-code": {
    eyebrow: "CLI harness",
    description: "Harness for Anthropic Claude Code sessions and project-aware coding workflows.",
    icon: Hexagon,
  },
  pi: {
    eyebrow: "CLI harness",
    description: "Harness for sessions backed by the Pi command-line runtime from pi.dev.",
    icon: Infinity,
  },
};

const fallbackHarnessMetadata: HarnessDisplayMetadata = {
  eyebrow: "Harness",
  description: "Harness runtime registered by the backend.",
  icon: Cable,
};

const prefsStore = usePreferencesStore();
const { harnesses: registeredHarnesses } = useHarnesses();

const expandedSettingsId = shallowRef<string | null>(null);

onMounted(async () => {
  await prefsStore.refresh();
});

const nucodeProvider = computed(() => prefsStore.get("nucode.provider", "copilot"));

const nucodeBaseUrl = computed(() => prefsStore.get("nucode.baseUrl", ""));
const defaultHarnessId = computed(() => prefsStore.get("defaultHarnessType", DEFAULT_HARNESS_TYPE));

const harnesses = computed<readonly HarnessCard[]>(() => {
  return registeredHarnesses.value.map(toHarnessCard);
});

const defaultHarness = computed(() => harnesses.value.find((harness) => harness.id === defaultHarnessId.value));

async function toggleHarness(harness: HarnessCard): Promise<void> {
  if (!harness.canToggle) return;
  await prefsStore.set(`${harness.id}.enabled`, harness.enabled ? "false" : "true");
}

async function makeDefaultHarness(harness: HarnessCard): Promise<void> {
  if (!harness.canDefault || !harness.enabled) return;
  await prefsStore.set("defaultHarnessType", harness.id);
}

function formatProvider(value: string): string {
  switch (value) {
    case "anthropic":
      return "Anthropic";
    case "openai":
      return "OpenAI";
    case "custom":
      return "Custom endpoint";
    case "copilot":
    default:
      return "GitHub Copilot";
  }
}

function toHarnessCard(harness: HarnessInfo): HarnessCard {
  const metadata = harnessDisplayMetadata[harness.type] ?? fallbackHarnessMetadata;
  const enabled = prefsStore.get(`${harness.type}.enabled`, harness.type === DEFAULT_HARNESS_TYPE ? "true" : "false") === "true";

  return {
    id: harness.type,
    name: harness.displayName,
    eyebrow: metadata.eyebrow,
    description: metadata.description,
    summary: summaryForHarness(harness, enabled),
    icon: metadata.icon,
    status: statusForHarness(harness, enabled),
    enabled,
    configurable: harness.type === "nucode" && enabled,
    canToggle: true,
    canDefault: true,
  };
}

function summaryForHarness(harness: HarnessInfo, enabled: boolean): string {
  if (!enabled) return "Disabled until enabled by the user.";
  if (!harness.available) return harness.reason ?? `${harness.displayName} is registered but not currently available.`;

  if (harness.type === "nucode") {
    return `${formatProvider(nucodeProvider.value)}`;
  }

  return `Available now. Uses the ${harness.displayName} runtime registered by the backend.`;
}

function statusForHarness(harness: HarnessInfo, enabled: boolean): HarnessStatus {
  if (!enabled) return "disabled";
  if (!harness.available) return "not-configured";
  if (harness.type === "nucode" && (nucodeProvider.value === "custom" && !nucodeBaseUrl.value)) {
    return "not-configured";
  }

  return "ready";
}

function statusLabel(status: HarnessStatus): string {
  switch (status) {
    case "ready":
      return "Ready";
    case "missing-credentials":
      return "Missing credentials";
    case "not-configured":
      return "Not configured";
    case "disabled":
      return "Disabled";
  }
}

function statusClasses(status: HarnessStatus): string {
  switch (status) {
    case "ready":
      return "border-green-500/30 bg-green-500/10 text-green-300";
    case "missing-credentials":
      return "border-yellow-500/30 bg-yellow-500/10 text-yellow-300";
    case "not-configured":
      return "border-border bg-main-bg text-muted";
    case "disabled":
      return "border-border bg-main-bg text-muted";
  }
}

function statusIcon(status: HarnessStatus): Component {
  switch (status) {
    case "ready":
      return CheckCircle2;
    case "missing-credentials":
      return AlertTriangle;
    case "not-configured":
    case "disabled":
      return CircleDashed;
  }
}
</script>

<template>
  <div class="grid gap-6">
    <section class="rounded-card border border-border bg-card-bg p-6 shadow-sm">
      <div class="flex flex-col gap-4 md:flex-row md:items-start md:justify-between">
        <div class="flex items-start gap-3">
          <div class="rounded-btn border border-border bg-main-bg p-2 text-text">
            <Cable
              :size="18"
              aria-hidden="true"
            />
          </div>
          <div class="space-y-2">
            <div class="flex flex-wrap items-center gap-2">
              <h2 class="text-lg font-semibold text-text">
                Harnesses
              </h2>
            </div>
            <p class="max-w-2xl text-sm text-muted">
              Harnesses define the runtimes Weave can use to drive sessions. NuCode now lives as one harness among several possible implementations.
            </p>
            <div class="flex flex-wrap items-center gap-2 pt-1 text-xs text-muted">
              <span>Default harness:</span>
              <span class="inline-flex items-center gap-1 rounded-full border border-accent/25 bg-accent/10 px-2 py-0.5 font-medium text-accent">
                <Star
                  :size="11"
                  aria-hidden="true"
                />
                {{ defaultHarness?.name ?? "None selected" }}
              </span>
            </div>
          </div>
        </div>
      </div>
    </section>

    <section class="grid gap-4">
      <article
        v-for="harness in harnesses"
        :key="harness.id"
        class="group rounded-card border border-border bg-card-bg p-5 shadow-sm transition-colors"
        :class="[
          harness.enabled ? 'hover:border-accent/50 hover:bg-panel-bg' : 'opacity-80',
          defaultHarnessId === harness.id ? 'border-accent/45' : '',
        ]"
      >
        <div class="flex flex-col gap-4 sm:flex-row sm:items-start sm:justify-between">
          <div class="flex min-w-0 items-start gap-4">
            <div class="rounded-2xl border border-border bg-main-bg p-3 text-text shadow-sm">
              <component
                :is="harness.icon"
                :size="20"
                aria-hidden="true"
              />
            </div>

            <div class="min-w-0 space-y-2">
              <div class="flex flex-wrap items-center gap-2">
                <p class="text-[11px] font-semibold uppercase tracking-[0.16em] text-muted">
                  {{ harness.eyebrow }}
                </p>
                <span
                  class="inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-[10px] font-medium"
                  :class="statusClasses(harness.status)"
                >
                  <component
                    :is="statusIcon(harness.status)"
                    :size="11"
                    aria-hidden="true"
                  />
                  {{ statusLabel(harness.status) }}
                </span>
                <span
                  v-if="defaultHarnessId === harness.id"
                  class="inline-flex items-center gap-1 rounded-full border border-accent/25 bg-accent/10 px-2 py-0.5 text-[10px] font-medium text-accent"
                >
                  <Star
                    :size="11"
                    aria-hidden="true"
                  />
                  Default
                </span>
              </div>

              <div>
                <h3 class="text-base font-semibold text-text">
                  {{ harness.name }}
                </h3>
                <p class="mt-1 text-sm text-muted">
                  {{ harness.description }}
                </p>
              </div>

              <p class="text-xs text-muted">
                {{ harness.summary }}
              </p>
            </div>
          </div>

          <div class="flex shrink-0 flex-col gap-3 sm:items-end">
            <button
              type="button"
              role="switch"
              :aria-checked="harness.enabled"
              :disabled="!harness.canToggle"
              class="inline-flex items-center gap-2 text-xs font-medium text-muted disabled:cursor-not-allowed disabled:opacity-50"
              @click="toggleHarness(harness)"
            >
              <span>{{ harness.enabled ? "Enabled" : "Disabled" }}</span>
              <span
                class="relative inline-flex h-6 w-11 shrink-0 rounded-full border-2 border-transparent transition-colors"
                :class="harness.enabled ? 'bg-accent' : 'bg-border'"
              >
                <span
                  class="pointer-events-none inline-block h-5 w-5 rounded-full bg-white shadow transition-transform"
                  :class="harness.enabled ? 'translate-x-5' : 'translate-x-0'"
                />
              </span>
            </button>

            <div class="flex flex-wrap items-center gap-2 sm:justify-end">
              <button
                type="button"
                class="inline-flex items-center gap-1 rounded-btn border border-border bg-main-bg px-2.5 py-1.5 text-xs font-medium text-text transition-colors hover:border-accent/50 disabled:cursor-not-allowed disabled:opacity-50"
                :disabled="!harness.canDefault || !harness.enabled || defaultHarnessId === harness.id"
                @click="makeDefaultHarness(harness)"
              >
                <Star
                  :size="12"
                  aria-hidden="true"
                />
                {{ defaultHarnessId === harness.id ? "Default" : "Set default" }}
              </button>

              <button
                v-if="harness.configurable"
                type="button"
                class="inline-flex items-center gap-1 rounded-btn border border-border bg-main-bg px-2.5 py-1.5 text-xs font-medium text-text transition-colors hover:border-accent/50"
                @click="expandedSettingsId = expandedSettingsId === harness.id ? null : harness.id"
              >
                <Settings2
                  :size="12"
                  aria-hidden="true"
                />
                Settings
              </button>
              <span
                v-else
                class="inline-flex items-center rounded-btn px-2.5 py-1.5 text-xs font-medium text-muted"
              >
                No settings yet
              </span>
            </div>
          </div>
        </div>

        <!-- Inline settings panel (NuCode only) -->
        <div
          v-if="expandedSettingsId === harness.id && harness.id === 'nucode'"
          class="mt-4 border-t border-border pt-4"
        >
          <NuCodeSettingsPanel />
        </div>
      </article>
    </section>
  </div>
</template>
