<script setup lang="ts">
import type { Component } from "vue";
import { computed, onBeforeUnmount, onMounted, shallowRef, watch } from "vue";
import { storeToRefs } from "pinia";
import { Cable, Download, LoaderCircle, RefreshCw, Star } from "lucide-vue-next";
import HarnessInstallPanel from "@/components/settings/HarnessInstallPanel.vue";
import HarnessProfilesPanel from "@/components/settings/HarnessProfilesPanel.vue";
import HarnessSignInPanel from "@/components/settings/HarnessSignInPanel.vue";
import HarnessUpdateStrip from "@/components/settings/HarnessUpdateStrip.vue";
import { refreshAllHarnesses, useHarnesses } from "@/composables/use-harnesses";
import { useAppShellStore } from "@/stores/app-shell";
import { useHarnessSetupStore } from "@/stores/harness-setup";
import { usePreferencesStore } from "@/stores/preferences";
import type { HarnessInfo } from "@/api/client";
import {
  harnessDisplay,
  harnessLocation,
  harnessState,
  harnessStatusClasses as statusClasses,
  harnessStatusIcon as statusIcon,
  harnessStatusLabel as statusLabel,
  type HarnessStatus,
} from "@/lib/harness-display";

interface HarnessCard {
  id: string;
  name: string;
  eyebrow: string;
  description: string;
  summary: string;
  /** Version and path of the executable Fleet found, when it found one. */
  location: string | null;
  icon: Component;
  status: HarnessStatus;
  enabled: boolean;
  canToggle: boolean;
  canDefault: boolean;
  supportsProfiles: boolean;
  /** Fleet can sign in to its providers: it supports that, Fleet runs without sign-in, and it's installed and working. */
  offersSignIn: boolean;
  /** The harness describes how it's installed here (OpenCode 2's install mode), shown under its card. */
  describesInstall: boolean;
  /** The harness as the server described it, for its update. */
  info: HarnessInfo;
}

const DEFAULT_HARNESS_TYPE = "opencode";
const POOLED_OPEN_CODE_MODE_PREFERENCE_KEY = "PooledOpenCodeHarness";

const prefsStore = usePreferencesStore();
const harnessSetup = useHarnessSetupStore();
const { config } = storeToRefs(useAppShellStore());
const { harnesses: registeredHarnesses, isLoading: isCheckingHarnesses, refresh: checkHarnessesAgain } = useHarnesses();

const isSavingPooledOpenCodeMode = shallowRef(false);
const pooledOpenCodeModeError = shallowRef<string | null>(null);

onMounted(async () => {
  await prefsStore.refresh();
});

const defaultHarnessId = computed(() => prefsStore.get("defaultHarnessType", DEFAULT_HARNESS_TYPE));
const isPooledOpenCodeModeEnabled = computed(
  () => prefsStore.get(POOLED_OPEN_CODE_MODE_PREFERENCE_KEY, "false") === "true",
);

const harnesses = computed<readonly HarnessCard[]>(() => {
  return registeredHarnesses.value.map(toHarnessCard);
});

const defaultHarness = computed(() => harnesses.value.find((harness) => harness.id === defaultHarnessId.value));

/** Off stops Fleet looking up harnesses' latest versions on npm (`HarnessEndpoints.UpdateChecksPreference`). */
const UPDATE_CHECKS_PREFERENCE_KEY = "harnessUpdates.check";
const areUpdateChecksOn = computed(() => prefsStore.get(UPDATE_CHECKS_PREFERENCE_KEY, "true") !== "false");

async function toggleUpdateChecks(): Promise<void> {
  await prefsStore.set(UPDATE_CHECKS_PREFERENCE_KEY, areUpdateChecksOn.value ? "false" : "true");
  refreshAllHarnesses();
}

/** While an update waits or runs, check on it every 2 seconds. */
const UPDATE_POLL_MS = 2000;
let updatePoll: ReturnType<typeof setInterval> | undefined;
const isUpdating = computed(() =>
  registeredHarnesses.value.some((harness) => harness.update?.job?.phase === "waiting" || harness.update?.job?.phase === "running"));

watch(isUpdating, (updating) => {
  if (updating && updatePoll === undefined) {
    updatePoll = setInterval(refreshAllHarnesses, UPDATE_POLL_MS);
  } else if (!updating && updatePoll !== undefined) {
    clearInterval(updatePoll);
    updatePoll = undefined;
  }
}, { immediate: true });

onBeforeUnmount(() => {
  if (updatePoll !== undefined) clearInterval(updatePoll);
});

async function toggleHarness(harness: HarnessCard): Promise<void> {
  if (!harness.canToggle) return;
  await prefsStore.set(`${harness.id}.enabled`, harness.enabled ? "false" : "true");
}

async function makeDefaultHarness(harness: HarnessCard): Promise<void> {
  if (!harness.canDefault || !harness.enabled) return;
  await prefsStore.set("defaultHarnessType", harness.id);
}

async function togglePooledOpenCodeMode(): Promise<void> {
  if (isSavingPooledOpenCodeMode.value) return;

  isSavingPooledOpenCodeMode.value = true;
  pooledOpenCodeModeError.value = null;

  try {
    await prefsStore.set(
      POOLED_OPEN_CODE_MODE_PREFERENCE_KEY,
      isPooledOpenCodeModeEnabled.value ? "false" : "true",
    );
  } catch (error) {
    pooledOpenCodeModeError.value = error instanceof Error
      ? error.message
      : "Failed to update pooled OpenCode mode.";
  } finally {
    isSavingPooledOpenCodeMode.value = false;
  }
}

function toHarnessCard(harness: HarnessInfo): HarnessCard {
  const metadata = harnessDisplay(harness.type);
  const enabled = prefsStore.get(`${harness.type}.enabled`, harness.type === DEFAULT_HARNESS_TYPE ? "true" : "false") === "true";

  return {
    id: harness.type,
    name: harness.displayName,
    eyebrow: metadata.eyebrow,
    description: metadata.description,
    summary: summaryForHarness(harness, enabled),
    location: harnessLocation(harness),
    icon: metadata.icon,
    status: statusForHarness(harness, enabled),
    enabled,
    canToggle: true,
    canDefault: true,
    supportsProfiles: harness.capabilities?.supportsProfiles === true,
    offersSignIn: harness.capabilities?.supportsProviderSignIn === true && harness.available,
    describesInstall: Boolean(harness.setup?.mode),
    info: harness,
  };
}

function summaryForHarness(harness: HarnessInfo, enabled: boolean): string {
  if (!enabled) return "Disabled until enabled by the user.";
  if (!harness.available) return harness.reason ?? `${harness.displayName} is registered but not currently available.`;

  return `Available now. Uses the ${harness.displayName} runtime registered by the backend.`;
}


function statusForHarness(harness: HarnessInfo, enabled: boolean): HarnessStatus {
  return enabled ? harnessState(harness) : "disabled";
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
              Harnesses define the runtimes Weave can use to drive sessions.
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

        <div class="flex shrink-0 flex-wrap items-start gap-2">
          <button
            v-if="!config.cloudMode"
            type="button"
            role="switch"
            :aria-checked="areUpdateChecksOn"
            class="inline-flex items-center gap-2 rounded-btn px-1 py-1.5 text-xs font-medium text-muted"
            data-testid="harnesses-update-checks"
            @click="void toggleUpdateChecks()"
          >
            <span>Check for updates</span>
            <span
              class="relative inline-flex h-5 w-9 shrink-0 rounded-full border-2 border-transparent transition-colors"
              :class="areUpdateChecksOn ? 'bg-accent' : 'bg-border'"
            >
              <span
                class="pointer-events-none inline-block h-4 w-4 rounded-full bg-white shadow transition-transform"
                :class="areUpdateChecksOn ? 'translate-x-4' : 'translate-x-0'"
              />
            </span>
          </button>
          <button
            v-if="!config.cloudMode"
            type="button"
            class="inline-flex items-center gap-1.5 rounded-btn border border-border bg-main-bg px-3 py-1.5 text-xs font-medium text-text transition-colors hover:border-accent/50"
            data-testid="harnesses-set-up"
            @click="harnessSetup.open('harnesses')"
          >
            <Download
              :size="12"
              aria-hidden="true"
            />
            Set up a harness
          </button>
          <!-- Fleet looks for each harness on every check, so one installed a moment ago shows up here. -->
          <button
            type="button"
            class="inline-flex shrink-0 items-center gap-1.5 self-start rounded-btn border border-border bg-main-bg px-3 py-1.5 text-xs font-medium text-text transition-colors hover:border-accent/50 disabled:cursor-not-allowed disabled:opacity-60"
            :disabled="isCheckingHarnesses"
            data-testid="harnesses-check-again"
            @click="void checkHarnessesAgain()"
          >
            <RefreshCw
              :size="12"
              :class="isCheckingHarnesses ? 'animate-spin' : ''"
              aria-hidden="true"
            />
            Check again
          </button>
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
              <p
                v-if="harness.location"
                class="break-all font-mono text-[11px] text-muted"
                data-testid="harness-location"
              >
                {{ harness.location }}
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

              <span
                v-if="harness.id !== 'opencode' && !harness.describesInstall"
                class="inline-flex items-center rounded-btn px-2.5 py-1.5 text-xs font-medium text-muted"
              >
                No settings yet
              </span>
            </div>
          </div>
        </div>

        <HarnessUpdateStrip
          v-if="harness.enabled && !config.cloudMode"
          class="mt-4"
          :harness="harness.info"
        />

        <HarnessInstallPanel
          v-if="harness.describesInstall && !config.cloudMode"
          class="mt-4"
          :harness="harness.info"
          :signs-in-here="harness.offersSignIn"
        />

        <!-- Listing providers starts the harness, so one that isn't turned on waits for a click. -->
        <HarnessSignInPanel
          v-if="harness.offersSignIn && !config.cloudMode"
          class="mt-4"
          :harness-type="harness.id"
          :harness-name="harness.name"
          :sign-in-command="harness.info.setup?.signInCommand ?? null"
          :auto-load="harness.enabled"
        />

        <div
          v-if="harness.id === 'opencode'"
          class="mt-4 border-t border-border pt-4"
          data-testid="pooled-opencode-mode-setting"
        >
          <div class="flex items-start justify-between gap-4 rounded-card border border-border bg-main-bg p-4">
            <div>
              <p class="text-sm font-medium text-text">
                Pooled OpenCode Mode
              </p>
              <p class="mt-1 text-xs text-muted">
                Off by default. When enabled, newly created OpenCode sessions use pooled automatic runtime mode and can prompt after Fleet restart without manual Resume. Existing sessions continue in their current mode.
              </p>
              <p
                v-if="pooledOpenCodeModeError"
                class="mt-2 text-xs text-red-300"
                role="alert"
              >
                {{ pooledOpenCodeModeError }}
              </p>
            </div>

            <div class="flex items-center gap-2">
              <LoaderCircle
                v-if="isSavingPooledOpenCodeMode"
                :size="16"
                class="animate-spin text-muted"
                aria-hidden="true"
              />
              <button
                type="button"
                role="switch"
                :aria-checked="isPooledOpenCodeModeEnabled"
                :disabled="prefsStore.isLoading || isSavingPooledOpenCodeMode"
                aria-label="Enable Pooled OpenCode Mode"
                class="relative inline-flex h-6 w-11 shrink-0 cursor-pointer rounded-full border-2 border-transparent transition-colors duration-200 ease-in-out focus:outline-none focus:ring-2 focus:ring-accent focus:ring-offset-2 focus:ring-offset-main-bg disabled:cursor-not-allowed disabled:opacity-60"
                :class="isPooledOpenCodeModeEnabled ? 'bg-accent' : 'bg-border'"
                data-testid="pooled-opencode-mode-toggle"
                @click="togglePooledOpenCodeMode"
              >
                <span
                  class="pointer-events-none inline-block h-5 w-5 rounded-full bg-white shadow ring-0 transition duration-200 ease-in-out"
                  :class="isPooledOpenCodeModeEnabled ? 'translate-x-5' : 'translate-x-0'"
                />
              </button>
            </div>
          </div>
        </div>

        <div
          v-if="harness.supportsProfiles"
          class="mt-4 border-t border-border pt-4"
        >
          <HarnessProfilesPanel
            :harness-type="harness.id"
            :harness-name="harness.name"
          />
        </div>
      </article>
    </section>
  </div>
</template>
