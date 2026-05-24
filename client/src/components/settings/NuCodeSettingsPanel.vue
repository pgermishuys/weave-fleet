<script setup lang="ts">
import { computed, onMounted, onUnmounted, shallowRef, watch } from "vue";
import { AlertCircle, Check, Copy, ExternalLink, LoaderCircle, X } from "lucide-vue-next";
import { usePreferencesStore } from "@/stores/preferences";
import { useNuCodeProviders } from "@/composables/use-nucode-providers";
import type { NuCodeProvider } from "@/lib/api-types";

// ── Constants ──────────────────────────────────────────────────────────────

const buttonSecondaryClass =
  "inline-flex items-center justify-center gap-2 rounded-btn border border-border bg-main-bg px-3 py-2 text-sm font-medium text-text transition-colors hover:border-accent/50 disabled:cursor-not-allowed disabled:opacity-60";
const buttonPrimaryClass =
  "inline-flex items-center justify-center gap-2 rounded-btn border border-accent bg-accent/10 px-3 py-2 text-sm font-medium text-accent transition-colors hover:bg-accent/20 disabled:cursor-not-allowed disabled:opacity-60";
const inputClass =
  "w-full rounded-btn border border-border bg-main-bg px-3 py-2 text-sm text-text outline-none transition-colors placeholder:text-muted focus:border-accent";
const selectClass =
  "w-full rounded-btn border border-border bg-main-bg px-3 py-2 text-sm text-text outline-none transition-colors focus:border-accent";

// ── State ──────────────────────────────────────────────────────────────────

const prefsStore = usePreferencesStore();
const {
  providers,
  isLoading: providersLoading,
  error: providersError,
  fetchProviders,
  storeCredentials,
  deleteCredentials,
  testConnection,
  requestDeviceCode,
  pollDeviceFlow,
} = useNuCodeProviders();

const selectedProviderId = shallowRef("");
const ready = shallowRef(false);

// Credential form state: fieldKey → value
const credentialFields = shallowRef<Record<string, string>>({});
const credSaving = shallowRef(false);
const credDisconnecting = shallowRef(false);
const credError = shallowRef<string | undefined>(undefined);
const credSuccess = shallowRef(false);

// Device flow state
const deviceFlowLoading = shallowRef(false);
const deviceFlowError = shallowRef<string | undefined>(undefined);
const deviceFlowUserCode = shallowRef("");
const deviceFlowVerificationUri = shallowRef("");
const deviceFlowDeviceCode = shallowRef("");
const deviceFlowExpiresIn = shallowRef(0);
const deviceFlowInterval = shallowRef(5);
const deviceFlowStatus = shallowRef<"idle" | "awaiting" | "complete" | "expired" | "denied" | "error">("idle");
const deviceFlowSecondsLeft = shallowRef(0);
const deviceFlowCopied = shallowRef(false);

// Connection test state
const testLoading = shallowRef(false);
const testResult = shallowRef<{ success: boolean; error?: string; latencyMs: number } | null>(null);

// Timer/interval handles — all cleared on unmount
const timers: ReturnType<typeof setTimeout>[] = [];
let pollIntervalId: ReturnType<typeof setTimeout> | null = null;
let countdownIntervalId: ReturnType<typeof setInterval> | null = null;

// ── Computed ───────────────────────────────────────────────────────────────

const selectedProvider = computed<NuCodeProvider | undefined>(
  () => providers.value.find((p) => p.id === selectedProviderId.value),
);

const secretFields = computed(() =>
  selectedProvider.value?.credentialFields.filter((f) => f.isSecret) ?? [],
);

const nonSecretFields = computed(() =>
  selectedProvider.value?.credentialFields.filter((f) => !f.isSecret) ?? [],
);

const isOAuthDevice = computed(() =>
  selectedProvider.value?.authMechanism === "OAuthDevice",
);

const isNoAuth = computed(() =>
  selectedProvider.value?.authMechanism === "None",
);

const canTest = computed<boolean>(
  () => !testLoading.value && !!selectedProvider.value,
);

const deviceFlowCountdownLabel = computed(() => {
  const mins = Math.floor(deviceFlowSecondsLeft.value / 60);
  const secs = deviceFlowSecondsLeft.value % 60;
  return `Polling every ${deviceFlowInterval.value}s. Code expires in ${mins}:${secs.toString().padStart(2, "0")}.`;
});

// ── Lifecycle ──────────────────────────────────────────────────────────────

onMounted(async () => {
  await prefsStore.refresh();
  const storedProvider = prefsStore.get("nucode.provider", "");

  await fetchProviders();

  const validId = providers.value.find((p) => p.id === storedProvider)?.id
    ?? providers.value[0]?.id
    ?? "";
  selectedProviderId.value = validId;
  ready.value = true;
});

watch(selectedProvider, () => {
  resetCredentialForm();
  resetDeviceFlow();
});

onUnmounted(() => {
  for (const t of timers) clearTimeout(t);
  timers.length = 0;
  stopPolling();
});

// ── Helpers ────────────────────────────────────────────────────────────────

function scheduleTimer(fn: () => void, ms: number): void {
  const t = setTimeout(() => {
    fn();
    const idx = timers.indexOf(t);
    if (idx !== -1) timers.splice(idx, 1);
  }, ms);
  timers.push(t);
}

function resetCredentialForm(): void {
  credentialFields.value = {};
  credError.value = undefined;
  credSuccess.value = false;
}

function resetDeviceFlow(): void {
  stopPolling();
  deviceFlowStatus.value = "idle";
  deviceFlowUserCode.value = "";
  deviceFlowVerificationUri.value = "";
  deviceFlowDeviceCode.value = "";
  deviceFlowError.value = undefined;
  deviceFlowCopied.value = false;
}

function stopPolling(): void {
  if (pollIntervalId !== null) {
    clearTimeout(pollIntervalId);
    pollIntervalId = null;
  }
  if (countdownIntervalId !== null) {
    clearInterval(countdownIntervalId);
    countdownIntervalId = null;
  }
}

async function onProviderChange(value: string): Promise<void> {
  selectedProviderId.value = value;
  await prefsStore.set("nucode.provider", value);
}

function setField(key: string, value: string): void {
  credentialFields.value = { ...credentialFields.value, [key]: value };
}

async function saveCredentials(): Promise<void> {
  if (!selectedProvider.value) return;
  credSaving.value = true;
  credError.value = undefined;
  credSuccess.value = false;

  try {
    await storeCredentials(selectedProvider.value.id, credentialFields.value);
    credSuccess.value = true;
    credentialFields.value = {};
    scheduleTimer(() => { credSuccess.value = false; }, 4_000);
  } catch (err) {
    credError.value = err instanceof Error ? err.message : "Failed to save credentials";
  } finally {
    credSaving.value = false;
  }
}

async function disconnectProvider(): Promise<void> {
  if (!selectedProvider.value) return;
  credDisconnecting.value = true;
  credError.value = undefined;

  try {
    await deleteCredentials(selectedProvider.value.id);
  } catch (err) {
    credError.value = err instanceof Error ? err.message : "Failed to disconnect";
  } finally {
    credDisconnecting.value = false;
  }
}

async function startDeviceFlow(): Promise<void> {
  if (!selectedProvider.value) return;
  deviceFlowLoading.value = true;
  deviceFlowError.value = undefined;
  deviceFlowStatus.value = "idle";

  try {
    const result = await requestDeviceCode(selectedProvider.value.id);
    deviceFlowUserCode.value = result.userCode;
    deviceFlowVerificationUri.value = result.verificationUri;
    deviceFlowDeviceCode.value = result.deviceCode;
    deviceFlowExpiresIn.value = result.expiresIn;
    deviceFlowInterval.value = result.interval;
    deviceFlowSecondsLeft.value = result.expiresIn;
    deviceFlowStatus.value = "awaiting";

    // Start countdown
    countdownIntervalId = setInterval(() => {
      deviceFlowSecondsLeft.value = Math.max(0, deviceFlowSecondsLeft.value - 1);
      if (deviceFlowSecondsLeft.value <= 0) {
        deviceFlowStatus.value = "expired";
        stopPolling();
      }
    }, 1_000);

    // Start polling
    startPolling();
  } catch (err) {
    deviceFlowError.value = err instanceof Error ? err.message : "Failed to start device flow";
    deviceFlowStatus.value = "error";
  } finally {
    deviceFlowLoading.value = false;
  }
}

function startPolling(): void {
  if (!selectedProvider.value) return;
  const providerId = selectedProvider.value.id;
  const deviceCode = deviceFlowDeviceCode.value;

  async function doPoll(): Promise<void> {
    try {
      const result = await pollDeviceFlow(providerId, deviceCode);
      console.log("[NuCode] Poll result:", JSON.stringify(result));
      if (result.status === "complete") {
        deviceFlowStatus.value = "complete";
        stopPolling();
        await fetchProviders();
        return;
      } else if (result.status === "expired") {
        deviceFlowStatus.value = "expired";
        deviceFlowError.value = result.message ?? "The code expired. Please try again.";
        stopPolling();
        return;
      } else if (result.status === "denied") {
        deviceFlowStatus.value = "denied";
        deviceFlowError.value = result.message ?? "Authorization was denied.";
        stopPolling();
        return;
      } else if (result.status === "error") {
        deviceFlowError.value = result.message ?? "An error occurred.";
        stopPolling();
        return;
      }
      // "pending" — schedule next poll, respecting updated interval from GitHub
      if (result.interval && result.interval > 0) {
        deviceFlowInterval.value = result.interval;
      }
      pollIntervalId = setTimeout(doPoll, deviceFlowInterval.value * 1_000);
    } catch (pollError) {
      console.error("[NuCode] Device flow poll error:", pollError);
      deviceFlowError.value = pollError instanceof Error ? pollError.message : "Poll request failed";
      deviceFlowStatus.value = "error";
      stopPolling();
    }
  }
  pollIntervalId = setTimeout(doPoll, deviceFlowInterval.value * 1_000);
}

async function copyUserCode(): Promise<void> {
  try {
    await navigator.clipboard.writeText(deviceFlowUserCode.value);
    deviceFlowCopied.value = true;
    scheduleTimer(() => { deviceFlowCopied.value = false; }, 2_000);
  } catch {
    // Clipboard API not available
  }
}

async function runConnectionTest(): Promise<void> {
  if (!canTest.value || !selectedProvider.value) return;

  testLoading.value = true;
  testResult.value = null;

  try {
    const result = await testConnection(selectedProvider.value.id);
    testResult.value = result;
  } catch (err) {
    testResult.value = {
      success: false,
      error: err instanceof Error ? err.message : "Request failed",
      latencyMs: 0,
    };
  } finally {
    testLoading.value = false;
    scheduleTimer(() => { testResult.value = null; }, 10_000);
  }
}
</script>

<template>
  <div class="space-y-5">
    <!-- Loading state -->
    <div
      v-if="!ready || providersLoading"
      class="flex items-center gap-2 text-sm text-muted"
    >
      <LoaderCircle
        :size="14"
        class="animate-spin"
        aria-hidden="true"
      />
      <span>Loading providers…</span>
    </div>

    <!-- Error state -->
    <div
      v-else-if="providersError"
      class="flex items-center gap-2 text-sm text-red-400"
    >
      <AlertCircle
        :size="14"
        aria-hidden="true"
      />
      <span>{{ providersError }}</span>
    </div>

    <!-- Empty state -->
    <div
      v-else-if="providers.length === 0"
      class="text-sm text-muted"
    >
      No providers available.
    </div>

    <template v-else>
      <!-- Provider selection -->
      <div class="space-y-1">
        <label class="text-xs font-medium uppercase tracking-wide text-muted">
          Provider
        </label>
        <select
          :value="selectedProviderId"
          :class="selectClass"
          @change="onProviderChange(($event.target as HTMLSelectElement).value)"
        >
          <option
            v-for="p in providers"
            :key="p.id"
            :value="p.id"
          >
            {{ p.displayName }}{{ p.isConnected ? ' ✓' : '' }}
          </option>
        </select>
        <p
          v-if="selectedProvider?.description"
          class="text-xs text-muted"
        >
          {{ selectedProvider.description }}
        </p>
      </div>

      <!-- Credentials section -->
      <div
        v-if="selectedProvider"
        class="rounded-card border border-border bg-main-bg p-4 space-y-3"
      >
        <p class="text-xs font-medium uppercase tracking-wide text-muted">
          Credentials
        </p>

        <!-- No auth required -->
        <div
          v-if="isNoAuth"
          class="flex items-center gap-2"
        >
          <Check
            :size="14"
            class="text-green-400"
            aria-hidden="true"
          />
          <span class="text-sm text-green-300">No credentials required</span>
        </div>

        <!-- OAuth device flow (e.g. GitHub Copilot) -->
        <template v-else-if="isOAuthDevice">
          <!-- Already connected -->
          <div
            v-if="selectedProvider.isConnected && deviceFlowStatus !== 'awaiting'"
            class="flex items-center justify-between"
          >
            <div class="flex items-center gap-2">
              <Check
                :size="14"
                class="text-green-400"
                aria-hidden="true"
              />
              <span class="text-sm text-green-300">Connected</span>
            </div>
            <button
              type="button"
              :class="buttonSecondaryClass"
              :disabled="credDisconnecting"
              @click="disconnectProvider"
            >
              {{ credDisconnecting ? 'Disconnecting…' : 'Disconnect' }}
            </button>
          </div>

          <!-- Device flow: idle — show connect button -->
          <div
            v-else-if="deviceFlowStatus === 'idle'"
            class="space-y-3"
          >
            <button
              type="button"
              :class="buttonPrimaryClass"
              :disabled="deviceFlowLoading"
              @click="startDeviceFlow"
            >
              <LoaderCircle
                v-if="deviceFlowLoading"
                :size="14"
                class="animate-spin"
                aria-hidden="true"
              />
              <span>{{ deviceFlowLoading ? 'Starting…' : `Connect with ${selectedProvider.displayName}` }}</span>
            </button>
            <p
              v-if="deviceFlowError"
              class="text-xs text-red-400"
            >
              {{ deviceFlowError }}
            </p>
          </div>

          <!-- Device flow: awaiting user authorization -->
          <div
            v-else-if="deviceFlowStatus === 'awaiting'"
            class="space-y-3"
          >
            <div class="space-y-1">
              <p class="text-xs font-medium uppercase tracking-wide text-muted">
                Device Code
              </p>
              <div class="flex items-center gap-3">
                <code class="rounded-btn border border-border bg-main-bg px-4 py-2 text-lg font-mono tracking-widest text-text">
                  {{ deviceFlowUserCode }}
                </code>
                <button
                  type="button"
                  :class="buttonSecondaryClass"
                  @click="copyUserCode"
                >
                  <Copy
                    :size="14"
                    aria-hidden="true"
                  />
                  {{ deviceFlowCopied ? 'Copied!' : 'Copy code' }}
                </button>
              </div>
            </div>

            <a
              :href="deviceFlowVerificationUri"
              target="_blank"
              rel="noopener noreferrer"
              class="inline-flex items-center gap-1 text-sm text-accent hover:underline"
            >
              <ExternalLink
                :size="14"
                aria-hidden="true"
              />
              Open GitHub
            </a>

            <div class="flex items-center gap-2 text-xs text-muted">
              <LoaderCircle
                :size="12"
                class="animate-spin"
                aria-hidden="true"
              />
              <span>{{ deviceFlowCountdownLabel }}</span>
            </div>

            <button
              type="button"
              :class="buttonSecondaryClass"
              @click="resetDeviceFlow"
            >
              Cancel
            </button>
          </div>

          <!-- Device flow: complete -->
          <div
            v-else-if="deviceFlowStatus === 'complete'"
            class="flex items-center gap-2"
          >
            <Check
              :size="14"
              class="text-green-400"
              aria-hidden="true"
            />
            <span class="text-sm text-green-300">Connected successfully.</span>
          </div>

          <!-- Device flow: expired / denied / error -->
          <div
            v-else
            class="space-y-3"
          >
            <div class="flex items-center gap-2">
              <AlertCircle
                :size="14"
                class="text-red-400"
                aria-hidden="true"
              />
              <span class="text-sm text-red-300">
                {{ deviceFlowError ?? 'Authorization failed.' }}
              </span>
            </div>
            <button
              type="button"
              :class="buttonSecondaryClass"
              @click="resetDeviceFlow"
            >
              Try again
            </button>
          </div>
        </template>

        <!-- API key / custom credential fields -->
        <template v-else>
          <!-- Connected status -->
          <div
            v-if="selectedProvider.isConnected"
            class="flex items-center justify-between"
          >
            <div class="flex items-center gap-2">
              <Check
                :size="14"
                class="text-green-400"
                aria-hidden="true"
              />
              <span class="text-sm text-green-300">Configured</span>
            </div>
            <button
              type="button"
              :class="buttonSecondaryClass"
              :disabled="credDisconnecting"
              @click="disconnectProvider"
            >
              {{ credDisconnecting ? 'Disconnecting…' : 'Disconnect' }}
            </button>
          </div>
          <div
            v-else-if="!selectedProvider.credentialOptional"
            class="flex items-center gap-2"
          >
            <AlertCircle
              :size="14"
              class="text-yellow-400"
              aria-hidden="true"
            />
            <span class="text-sm text-yellow-300">Not configured</span>
          </div>
          <div
            v-else
            class="flex items-center gap-2"
          >
            <Check
              :size="14"
              class="text-green-400"
              aria-hidden="true"
            />
            <span class="text-sm text-green-300">Credentials optional</span>
          </div>

          <!-- Secret credential fields (e.g. API key) -->
          <div
            v-for="field in secretFields"
            :key="field.key"
            class="space-y-1"
          >
            <label class="text-xs font-medium text-muted">{{ field.displayName }}</label>
            <input
              :value="credentialFields[field.key] ?? ''"
              type="password"
              :class="inputClass"
              :placeholder="field.helpText ?? ''"
              :required="field.required"
              autocomplete="off"
              @input="setField(field.key, ($event.target as HTMLInputElement).value)"
            >
          </div>

          <!-- Non-secret fields (e.g. baseUrl, resourceName) -->
          <div
            v-for="field in nonSecretFields"
            :key="field.key"
            class="space-y-1"
          >
            <label class="text-xs font-medium text-muted">{{ field.displayName }}</label>
            <input
              :value="credentialFields[field.key] ?? ''"
              type="text"
              :class="inputClass"
              :placeholder="field.helpText ?? ''"
              :required="field.required"
              @input="setField(field.key, ($event.target as HTMLInputElement).value)"
            >
          </div>

          <!-- Save button -->
          <div
            v-if="secretFields.length || nonSecretFields.length"
            class="flex items-center gap-3"
          >
            <button
              type="button"
              :class="buttonSecondaryClass"
              :disabled="credSaving"
              @click="saveCredentials"
            >
              <LoaderCircle
                v-if="credSaving"
                :size="14"
                class="animate-spin"
                aria-hidden="true"
              />
              <span>{{ credSaving ? 'Saving…' : 'Save' }}</span>
            </button>
            <span
              v-if="credSuccess"
              class="flex items-center gap-1 text-sm text-green-300"
            >
              <Check
                :size="14"
                aria-hidden="true"
              />
              Saved
            </span>
            <span
              v-if="credError"
              class="text-sm text-red-400"
            >
              {{ credError }}
            </span>
          </div>
        </template>
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
    </template>
  </div>
</template>
