<script setup lang="ts">
import type { Component } from "vue";
import { computed, onMounted, shallowRef } from "vue";
import {
  AlertTriangle,
  ArrowLeft,
  Cable,
  CheckCircle2,
  ChevronRight,
  CircleDashed,
  Cpu,
  Hexagon,
  Infinity,
  Star,
  TerminalSquare,
} from "lucide-vue-next";
import NuCodeSection from "@/components/settings/NuCodeSection.vue";
import { useHarnesses } from "@/composables/use-harnesses";
import { usePreferencesStore } from "@/stores/preferences";

type HarnessId = "nucode" | "opencode" | "claude-code" | "pi";
type HarnessStatus = "ready" | "missing-credentials" | "not-configured" | "disabled" | "coming-soon";

interface HarnessCard {
  id: HarnessId;
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

const prefsStore = usePreferencesStore();
const { harnesses: registeredHarnesses } = useHarnesses();
const selectedHarness = shallowRef<HarnessId | null>(null);

onMounted(async () => {
  await prefsStore.refresh();
});

const nucodeEnabled = computed(() => prefsStore.get("nucode.enabled", "false") === "true");
const nucodeProvider = computed(() => prefsStore.get("nucode.provider", "copilot"));
const nucodeModel = computed(() => prefsStore.get("nucode.modelId", ""));
const nucodeBaseUrl = computed(() => prefsStore.get("nucode.baseUrl", ""));
const opencodeEnabled = computed(() => prefsStore.get("opencode.enabled", "true") === "true");
const opencodeInfo = computed(() => registeredHarnesses.value.find((harness) => harness.type === "opencode"));
const defaultHarnessId = computed<HarnessId>(() => {
  const stored = prefsStore.get("defaultHarnessType", "opencode");
  return stored === "opencode" || stored === "claude-code" || stored === "pi"
    ? stored
    : "nucode";
});

const nucodeStatus = computed<HarnessStatus>(() => {
  if (!nucodeEnabled.value) return "disabled";
  if (!nucodeModel.value) return "not-configured";
  if (nucodeProvider.value === "custom" && !nucodeBaseUrl.value) return "not-configured";
  return "ready";
});

const opencodeStatus = computed<HarnessStatus>(() => {
  if (!opencodeEnabled.value) return "disabled";
  return opencodeInfo.value?.available === false ? "not-configured" : "ready";
});

const harnesses = computed<readonly HarnessCard[]>(() => [
  {
    id: "nucode",
    name: "NuCode",
    eyebrow: "Local harness",
    description: "In-process AI coding harness for sessions that run inside Weave.",
    summary: nucodeEnabled.value
      ? `${formatProvider(nucodeProvider.value)}${nucodeModel.value ? ` · ${nucodeModel.value}` : " · model not selected"}`
      : "Disabled until enabled by the user.",
    icon: Cpu,
    status: nucodeStatus.value,
    enabled: nucodeEnabled.value,
    configurable: true,
    canToggle: true,
    canDefault: true,
  },
  {
    id: "opencode",
    name: "OpenCode",
    eyebrow: "CLI harness",
    description: "Harness for sessions backed by the OpenCode command-line runtime.",
    summary: opencodeInfo.value?.available === false
      ? opencodeInfo.value.reason ?? "OpenCode is registered but not currently available."
      : "Available now. Uses the OpenCode CLI runtime registered by the backend.",
    icon: TerminalSquare,
    status: opencodeStatus.value,
    enabled: opencodeEnabled.value,
    configurable: false,
    canToggle: true,
    canDefault: true,
  },
  {
    id: "claude-code",
    name: "Claude Code",
    eyebrow: "CLI harness",
    description: "Harness for Anthropic Claude Code sessions and project-aware coding workflows.",
    summary: "Future settings could include executable discovery, account status, permissions, and working-directory policy.",
    icon: Hexagon,
    status: "coming-soon",
    enabled: false,
    configurable: false,
    canToggle: false,
    canDefault: false,
  },
  {
    id: "pi",
    name: "Pi",
    eyebrow: "CLI harness",
    description: "Harness for sessions backed by the Pi command-line runtime from pi.dev.",
    summary: "Future settings could include executable discovery, account status, permissions, and launch options.",
    icon: Infinity,
    status: "coming-soon",
    enabled: false,
    configurable: false,
    canToggle: false,
    canDefault: false,
  },
]);

const activeHarness = computed(() => harnesses.value.find((harness) => harness.id === selectedHarness.value));
const defaultHarness = computed(() => harnesses.value.find((harness) => harness.id === defaultHarnessId.value));

function selectHarness(harness: HarnessCard): void {
  if (!harness.configurable) return;
  selectedHarness.value = harness.id;
}

function showHarnessList(): void {
  selectedHarness.value = null;
}

async function toggleHarness(harness: HarnessCard): Promise<void> {
  if (!harness.canToggle) return;

  if (harness.id === "nucode") {
    await prefsStore.set("nucode.enabled", harness.enabled ? "false" : "true");
    return;
  }

  if (harness.id === "opencode") {
    await prefsStore.set("opencode.enabled", harness.enabled ? "false" : "true");
  }
}

async function makeDefaultHarness(harness: HarnessCard): Promise<void> {
  if (!harness.canDefault || !harness.enabled) return;
  await prefsStore.set("defaultHarnessType", harness.id);
}

function handleCardClick(harness: HarnessCard, event: MouseEvent): void {
  const target = event.target;
  if (target instanceof Element && target.closest("button")) return;
  selectHarness(harness);
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
    case "coming-soon":
      return "Coming soon";
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
    case "coming-soon":
      return "border-accent/25 bg-accent/10 text-accent";
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
    case "coming-soon":
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

    <template v-if="selectedHarness === null">
      <section class="grid gap-4">
        <article
          v-for="harness in harnesses"
          :key="harness.id"
          class="group rounded-card border border-border bg-card-bg p-5 shadow-sm transition-colors"
          :class="[
            harness.configurable ? 'cursor-pointer hover:border-accent/50 hover:bg-panel-bg' : 'opacity-80',
            defaultHarnessId === harness.id ? 'border-accent/45' : '',
          ]"
          @click="handleCardClick(harness, $event)"
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
                  type="button"
                  class="inline-flex items-center gap-1 rounded-btn px-2.5 py-1.5 text-xs font-medium text-muted transition-colors hover:bg-main-bg hover:text-text disabled:cursor-not-allowed disabled:opacity-50"
                  :disabled="!harness.configurable"
                  @click="selectHarness(harness)"
                >
                  <span>{{ harness.configurable ? "Configure" : "No settings" }}</span>
                  <ChevronRight
                    v-if="harness.configurable"
                    :size="14"
                    class="transition-transform group-hover:translate-x-0.5"
                    aria-hidden="true"
                  />
                </button>
              </div>
            </div>
          </div>
        </article>
      </section>

    </template>

    <template v-else>
      <section class="rounded-card border border-border bg-card-bg p-4 shadow-sm">
        <button
          type="button"
          class="inline-flex items-center gap-2 text-sm font-medium text-muted transition-colors hover:text-text"
          @click="showHarnessList"
        >
          <ArrowLeft
            :size="14"
            aria-hidden="true"
          />
          Harnesses
        </button>

        <div
          v-if="activeHarness"
          class="mt-4 flex flex-wrap items-center gap-2"
        >
          <p class="text-[11px] font-semibold uppercase tracking-[0.16em] text-muted">
            {{ activeHarness.eyebrow }}
          </p>
          <ChevronRight
            :size="12"
            class="text-muted"
            aria-hidden="true"
          />
          <p class="text-sm font-semibold text-text">
            {{ activeHarness.name }}
          </p>
        </div>
      </section>

      <NuCodeSection v-if="selectedHarness === 'nucode'" />
    </template>
  </div>
</template>
