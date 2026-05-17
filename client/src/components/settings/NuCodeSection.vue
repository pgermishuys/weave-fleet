<script setup lang="ts">
import { computed, onMounted, shallowRef, watch } from "vue";
import { AlertCircle, Check, ChevronDown, ChevronRight, Cpu, LoaderCircle, X } from "lucide-vue-next";
import { apiFetch } from "@/lib/api-client";
import type { CredentialSummary } from "@/lib/api-types";
import { usePreferencesStore } from "@/stores/preferences";
import { useSettingsNav } from "@/composables/use-settings-nav";

// ── Constants ──────────────────────────────────────────────────────────────

const buttonPrimaryClass =
  "inline-flex items-center justify-center gap-2 rounded-btn bg-primary px-3 py-2 text-sm font-medium text-white transition-opacity hover:opacity-90 disabled:cursor-not-allowed disabled:opacity-60";
const buttonSecondaryClass =
  "inline-flex items-center justify-center gap-2 rounded-btn border border-border bg-main-bg px-3 py-2 text-sm font-medium text-text transition-colors hover:border-accent/50 disabled:cursor-not-allowed disabled:opacity-60";
const inputClass =
  "w-full rounded-btn border border-border bg-main-bg px-3 py-2 text-sm text-text outline-none transition-colors placeholder:text-muted focus:border-accent";
const selectClass =
  "w-full rounded-btn border border-border bg-main-bg px-3 py-2 text-sm text-text outline-none transition-colors focus:border-accent";

type Provider = "copilot" | "anthropic" | "openai" | "custom";

interface ProviderOption {
  value: Provider;
  label: string;
  credNamespace: string;
  credKind: string;
  helpText: string;
  defaultModels: readonly string[];
  defaultBaseUrl: string;
}

const PROVIDERS: readonly ProviderOption[] = [
  {
    value: "copilot",
    label: "GitHub Copilot",
    credNamespace: "github",
    credKind: "oauth-access-token",
    helpText: "Uses your GitHub OAuth token — connect GitHub in Settings → Credentials.",
    defaultModels: ["claude-sonnet-4-20250514", "gpt-4o"],
    defaultBaseUrl: "https://api.githubcopilot.com/",
  },
  {
    value: "anthropic",
    label: "Anthropic",
    credNamespace: "anthropic",
    credKind: "api-key",
    helpText: "Requires an Anthropic API key.",
    defaultModels: ["claude-sonnet-4-20250514", "claude-opus-4-20250514"],
    defaultBaseUrl: "https://api.anthropic.com/v1/",
  },
  {
    value: "openai",
    label: "OpenAI",
    credNamespace: "openai",
    credKind: "api-key",
    helpText: "Requires an OpenAI API key.",
    defaultModels: ["gpt-4o", "o3"],
    defaultBaseUrl: "https://api.openai.com/v1/",
  },
  {
    value: "custom",
    label: "Custom (OpenAI-compatible)",
    credNamespace: "custom",
    credKind: "api-key",
    helpText: "OpenAI-compatible endpoint (e.g. Ollama, OpenRouter). API key optional for local models.",
    defaultModels: ["llama3", "mistral"],
    defaultBaseUrl: "http://localhost:11434/v1",
  },
];

// ── State ──────────────────────────────────────────────────────────────────

const prefsStore = usePreferencesStore();
const { setActiveSection } = useSettingsNav();

const enabled = shallowRef(false);
const provider = shallowRef<Provider>("copilot");
const modelId = shallowRef("");
const baseUrl = shallowRef("");
const showAdvanced = shallowRef(false);

const credentials = shallowRef<CredentialSummary[]>([]);
const credentialsLoading = shallowRef(false);

const testLoading = shallowRef(false);
const testResult = shallowRef<{ success: boolean; error?: string; latencyMs: number } | null>(null);
let testClearTimer: ReturnType<typeof setTimeout> | null = null;

// ── Computed ───────────────────────────────────────────────────────────────

const selectedProvider = computed<ProviderOption>(
  () => PROVIDERS.find((p) => p.value === provider.value) ?? PROVIDERS[0],
);

const credentialConfigured = computed<boolean>(() => {
  const p = selectedProvider.value;
  return credentials.value.some(
    (c) => c.namespace === p.credNamespace && c.kind === p.credKind,
  );
});

const readinessStatus = computed<"ready" | "missing-credentials" | "not-configured">(() => {
  if (!provider.value || !modelId.value) return "not-configured";
  if (!credentialConfigured.value && provider.value !== "custom") return "missing-credentials";
  if (provider.value === "custom" && !baseUrl.value) return "not-configured";
  return "ready";
});

const canTest = computed<boolean>(
  () => enabled.value && readinessStatus.value === "ready" && !testLoading.value,
);

// ── Lifecycle ──────────────────────────────────────────────────────────────

onMounted(async () => {
  await prefsStore.refresh();

  enabled.value = prefsStore.get("nucode.enabled", "false") === "true";
  provider.value = (prefsStore.get("nucode.provider", "copilot") as Provider) ?? "copilot";
  modelId.value = prefsStore.get("nucode.modelId", "");
  baseUrl.value = prefsStore.get("nucode.baseUrl", "");

  await loadCredentials();
});

// Reload credential status when provider changes
watch(provider, () => {
  void loadCredentials();
});

// ── Helpers ────────────────────────────────────────────────────────────────

async function loadCredentials(): Promise<void> {
  credentialsLoading.value = true;
  try {
    const response = await apiFetch("/api/credentials");
    if (response.ok) {
      credentials.value = (await response.json()) as CredentialSummary[];
    }
  } catch {
    // Silently ignore — credential status will show as unknown
  } finally {
    credentialsLoading.value = false;
  }
}

// ── Preference setters ─────────────────────────────────────────────────────

async function toggleEnabled(): Promise<void> {
  enabled.value = !enabled.value;
  await prefsStore.set("nucode.enabled", enabled.value ? "true" : "false");
}

async function onProviderChange(value: string): Promise<void> {
  provider.value = value as Provider;
  await prefsStore.set("nucode.provider", value);
}

async function onModelIdChange(value: string): Promise<void> {
  modelId.value = value;
  await prefsStore.set("nucode.modelId", value);
}

async function applyModelPreset(preset: string): Promise<void> {
  await onModelIdChange(preset);
}

async function onBaseUrlChange(value: string): Promise<void> {
  baseUrl.value = value;
  await prefsStore.set("nucode.baseUrl", value);
}

// ── Connection test ────────────────────────────────────────────────────────

interface TestConnectionResponse {
  success: boolean;
  error?: string;
  latencyMs: number;
}

async function runConnectionTest(): Promise<void> {
  if (!canTest.value) return;

  if (testClearTimer !== null) {
    clearTimeout(testClearTimer);
    testClearTimer = null;
  }

  testLoading.value = true;
  testResult.value = null;

  try {
    const response = await apiFetch("/api/nucode/test-connection", { method: "POST" });
    const body = (await response.json()) as TestConnectionResponse;
    testResult.value = { success: body.success, error: body.error, latencyMs: body.latencyMs };
  } catch (error) {
    testResult.value = {
      success: false,
      error: error instanceof Error ? error.message : "Request failed",
      latencyMs: 0,
    };
  } finally {
    testLoading.value = false;
    testClearTimer = setTimeout(() => {
      testResult.value = null;
    }, 10_000);
  }
}

function navigateToCredentials(): void {
  setActiveSection("credentials");
}
</script>

<template>
  <section class="rounded-card border border-border bg-card-bg p-6 shadow-sm">
    <!-- Header -->
    <div class="flex flex-col gap-3 md:flex-row md:items-start md:justify-between">
      <div class="flex items-center gap-3">
        <div class="rounded-btn border border-border bg-main-bg p-2 text-text">
          <Cpu
            :size="16"
            aria-hidden="true"
          />
        </div>
        <div class="space-y-1">
          <div class="flex items-center gap-2">
            <h2 class="text-lg font-semibold text-text">
              NuCode
            </h2>
            <!-- Readiness badge -->
            <span
              v-if="enabled && readinessStatus === 'ready'"
              class="rounded-full border border-green-500/30 bg-green-500/10 px-2 py-0.5 text-[10px] font-medium text-green-300"
            >
              Ready
            </span>
            <span
              v-else-if="enabled && readinessStatus === 'missing-credentials'"
              class="rounded-full border border-yellow-500/30 bg-yellow-500/10 px-2 py-0.5 text-[10px] font-medium text-yellow-300"
            >
              Missing credentials
            </span>
            <span
              v-else-if="enabled && readinessStatus === 'not-configured'"
              class="rounded-full border border-border px-2 py-0.5 text-[10px] font-medium text-muted"
            >
              Not configured
            </span>
          </div>
          <p class="text-sm text-muted">
            In-process AI coding agent. Enable to make NuCode available as a harness.
          </p>
        </div>
      </div>

      <!-- Enable toggle -->
      <button
        type="button"
        :class="enabled ? buttonPrimaryClass : buttonSecondaryClass"
        @click="toggleEnabled"
      >
        {{ enabled ? "Enabled" : "Disabled" }}
      </button>
    </div>

    <!-- Configuration (visible only when enabled) -->
    <div
      v-if="enabled"
      class="mt-6 space-y-5"
    >
      <!-- Provider selection -->
      <div class="space-y-1">
        <label class="text-xs font-medium uppercase tracking-wide text-muted">
          Provider
        </label>
        <select
          :value="provider"
          :class="selectClass"
          @change="onProviderChange(($event.target as HTMLSelectElement).value)"
        >
          <option
            v-for="p in PROVIDERS"
            :key="p.value"
            :value="p.value"
          >
            {{ p.label }}
          </option>
        </select>
        <p class="text-xs text-muted">
          {{ selectedProvider.helpText }}
        </p>
      </div>

      <!-- Model ID input with presets -->
      <div class="space-y-2">
        <label class="text-xs font-medium uppercase tracking-wide text-muted">
          Model
        </label>
        <input
          :value="modelId"
          type="text"
          :class="inputClass"
          :placeholder="`e.g. ${selectedProvider.defaultModels[0]}`"
          @input="onModelIdChange(($event.target as HTMLInputElement).value)"
        >
        <div class="flex flex-wrap gap-2">
          <button
            v-for="preset in selectedProvider.defaultModels"
            :key="preset"
            type="button"
            class="rounded-full border border-border px-3 py-1 text-xs text-text transition-colors hover:border-accent/50"
            @click="applyModelPreset(preset)"
          >
            {{ preset }}
          </button>
        </div>
      </div>

      <!-- Credential status -->
      <div class="rounded-card border border-border bg-main-bg p-4">
        <p class="text-xs font-medium uppercase tracking-wide text-muted">
          Credential status
        </p>
        <div
          v-if="credentialsLoading"
          class="mt-2 flex items-center gap-2 text-sm text-muted"
        >
          <LoaderCircle
            :size="14"
            class="animate-spin"
            aria-hidden="true"
          />
          <span>Checking…</span>
        </div>
        <div
          v-else-if="credentialConfigured || provider === 'custom'"
          class="mt-2 flex items-center gap-2"
        >
          <Check
            :size="14"
            class="text-green-400"
            aria-hidden="true"
          />
          <span class="text-sm text-green-300">
            {{ provider === 'custom' ? 'Custom provider (API key optional)' : 'Configured' }}
          </span>
        </div>
        <div
          v-else
          class="mt-2 flex items-center gap-2"
        >
          <AlertCircle
            :size="14"
            class="text-yellow-400"
            aria-hidden="true"
          />
          <span class="text-sm text-yellow-300">
            Missing —
            <button
              type="button"
              class="underline hover:no-underline"
              @click="navigateToCredentials"
            >
              Add in Credentials
            </button>
          </span>
        </div>
      </div>

      <!-- Advanced: Custom endpoint URL -->
      <div>
        <button
          type="button"
          class="flex items-center gap-1 text-xs font-medium text-muted hover:text-text"
          @click="showAdvanced = !showAdvanced"
        >
          <component
            :is="showAdvanced ? ChevronDown : ChevronRight"
            :size="12"
            aria-hidden="true"
          />
          Advanced
        </button>

        <div
          v-if="showAdvanced"
          class="mt-3 space-y-1"
        >
          <label class="text-xs font-medium uppercase tracking-wide text-muted">
            Custom endpoint URL
          </label>
          <input
            :value="baseUrl"
            type="url"
            :class="inputClass"
            :placeholder="selectedProvider.defaultBaseUrl"
            @input="onBaseUrlChange(($event.target as HTMLInputElement).value)"
          >
          <p class="text-xs text-muted">
            Override the default endpoint (useful for proxies, Helicone, or self-hosted models).
          </p>
        </div>
      </div>

      <!-- Connection test -->
      <div class="flex flex-wrap items-center gap-3">
        <button
          type="button"
          :class="buttonSecondaryClass"
          :disabled="!canTest"
          @click="runConnectionTest"
        >
          <LoaderCircle
            v-if="testLoading"
            :size="16"
            class="animate-spin"
            aria-hidden="true"
          />
          <span>{{ testLoading ? "Testing…" : "Test Connection" }}</span>
        </button>

        <div
          v-if="testResult !== null"
          class="flex items-center gap-2"
        >
          <Check
            v-if="testResult.success"
            :size="14"
            class="text-green-400"
            aria-hidden="true"
          />
          <X
            v-else
            :size="14"
            class="text-red-400"
            aria-hidden="true"
          />
          <span
            v-if="testResult.success"
            class="text-sm text-green-300"
          >
            Connected ({{ testResult.latencyMs }}ms)
          </span>
          <span
            v-else
            class="text-sm text-red-300"
          >
            {{ testResult.error ?? "Connection failed" }}
          </span>
        </div>
      </div>
    </div>
  </section>
</template>
