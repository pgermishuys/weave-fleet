<script setup lang="ts">
import type { Component } from "vue";
import { computed, nextTick, onBeforeUnmount, onMounted, shallowRef, watch } from "vue";
import { storeToRefs } from "pinia";
import { Download, RefreshCw, Star } from "lucide-vue-next";
import HarnessInstallPanel from "@/components/settings/HarnessInstallPanel.vue";
import HarnessPathField from "@/components/settings/HarnessPathField.vue";
import HarnessProfilesPanel from "@/components/settings/HarnessProfilesPanel.vue";
import HarnessSignInPanel from "@/components/settings/HarnessSignInPanel.vue";
import HarnessUpdateStrip from "@/components/settings/HarnessUpdateStrip.vue";
import HarnessWeaveGroup from "@/components/settings/HarnessWeaveGroup.vue";
import SettingGroup from "@/components/settings/SettingGroup.vue";
import SettingRow from "@/components/settings/SettingRow.vue";
import SettingSwitch from "@/components/settings/SettingSwitch.vue";
import { Button } from "@/components/ui/button";
import { useAgentBrowserSettings } from "@/composables/use-agent-browser-settings";
import { useSettingsNav } from "@/composables/use-settings-nav";
import { useWeaveConfig } from "@/composables/use-weave-config";
import { PERMISSION_LEVEL_NAMES } from "@/lib/permissions";
import { PERMISSION_LEVELS, useHarnessPermissions } from "@/composables/use-harness-permissions";
import { forgetHarnessCatalogs } from "@/composables/use-harness-catalog";
import { refreshAllHarnesses, useHarnesses } from "@/composables/use-harnesses";
import { useAppShellStore } from "@/stores/app-shell";
import { useHarnessSetupStore } from "@/stores/harness-setup";
import { usePreferencesStore } from "@/stores/preferences";
import type { HarnessInfo, HarnessSetting } from "@/api/client";
import { DEFAULT_HARNESS_PREFERENCE_KEY, resolveDefaultHarness } from "@/composables/use-enabled-harnesses";
import { formatRelativeTime } from "@/lib/format-utils";
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
  /** Its own switches, as the harness describes them. */
  settings: readonly HarnessSetting[];
  /** The harness as the server described it, for its update. */
  info: HarnessInfo;
  /** Fleet can hand it a permission level, and its agents get Fleet's browser. */
  supportsPermissionLevels: boolean;
  supportsBrowser: boolean;
}

const prefsStore = usePreferencesStore();
const harnessSetup = useHarnessSetupStore();
const { config } = storeToRefs(useAppShellStore());
const { harnesses: registeredHarnesses, isLoading: isCheckingHarnesses, refresh: checkHarnessesAgain } = useHarnesses();

/** The harness switch being saved (its preference key), and the last save that failed. */
const savingSetting = shallowRef<string | null>(null);
const settingError = shallowRef<{ key: string; message: string } | null>(null);

onMounted(async () => {
  // The list shows what Fleet found last at once; opening Settings checks every harness again behind it.
  void checkHarnessesAgain();
  await prefsStore.refresh();
});

/** When Fleet last checked, e.g. "Checked 2m ago". Missing from a Fleet that checks on every request. */
const now = shallowRef(Date.now());
const clock = setInterval(() => {
  now.value = Date.now();
}, 15_000);
const checkedLabel = computed(() => {
  const checkedAt = registeredHarnesses.value[0]?.checkedAt;
  if (!checkedAt) return null;
  return `Checked ${formatRelativeTime(checkedAt, now.value)}`;
});

const defaultHarnessId = computed(() =>
  resolveDefaultHarness(prefsStore.get(DEFAULT_HARNESS_PREFERENCE_KEY, ""), registeredHarnesses.value));

/** A switch is on as saved; unset (or empty), it's what the harness says its default is. */
function isSettingOn(setting: HarnessSetting): boolean {
  const saved = prefsStore.get(setting.key, "");
  return saved === "" ? setting.default : saved === "true";
}

const harnesses = computed<readonly HarnessCard[]>(() => {
  return registeredHarnesses.value.map(toHarnessCard);
});

const weave = useWeaveConfig();
const permissions = useHarnessPermissions();
const { settings: browserSettings, load: loadBrowser } = useAgentBrowserSettings();
const { activeHarness, activeAnchor } = useSettingsNav();

onMounted(() => {
  void weave.load();
  void loadBrowser();
});

/** The tab shown: the one picked (remembered while Settings is open), else the default harness's, else the first. */
const selected = computed(() =>
  harnesses.value.find((harness) => harness.id === activeHarness.value)
  ?? harnesses.value.find((harness) => harness.id === defaultHarnessId.value)
  ?? harnesses.value[0]);

function selectHarness(id: string): void {
  activeHarness.value = id;
}

// The old Weave page lands here with no harness: the default's tab if Weave is asked about there, else the first that is.
watch([activeAnchor, activeHarness, () => weave.view.value, harnesses], () => {
  if (activeAnchor.value !== "weave" || activeHarness.value !== null || !weave.view.value) return;
  const detected = new Set(weave.view.value.harnesses.map((detection) => detection.harnessType));
  const pick = detected.has(defaultHarnessId.value)
    ? defaultHarnessId.value
    : harnesses.value.find((harness) => detected.has(harness.id))?.id;
  if (pick) activeHarness.value = pick;
}, { immediate: true });

// Scroll to the group a link named (Weave, What it may do) once it is on the page; Weave's arrives after Fleet asks.
// Once there, the anchor is spent, so a later save or refresh doesn't pull the page back to it.
watch([activeAnchor, selected, () => weave.view.value], async () => {
  if (!activeAnchor.value) return;
  await nextTick();
  const group = document.querySelector(`[data-setting-group="${activeAnchor.value.replace(/"/g, "")}"]`);
  if (!group) return;
  group.scrollIntoView?.({ block: "start", behavior: "smooth" });
  activeAnchor.value = null;
}, { flush: "post" });

function dotClass(status: HarnessStatus): string {
  switch (status) {
    case "ready":
      return "bg-running";
    case "sign-in-required":
    case "update-needed":
    case "not-working":
      return "bg-idle";
    default:
      return "bg-muted/50";
  }
}

async function changeDefault(event: Event): Promise<void> {
  const target = harnesses.value.find((harness) => harness.id === (event.target as HTMLSelectElement).value);
  if (target) await makeDefaultHarness(target);
}

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
  clearInterval(clock);
});

async function toggleHarness(harness: HarnessCard): Promise<void> {
  if (!harness.canToggle) return;
  await prefsStore.set(`${harness.id}.enabled`, harness.enabled ? "false" : "true");
}

async function makeDefaultHarness(harness: HarnessCard): Promise<void> {
  if (!harness.canDefault || !harness.enabled) return;
  await prefsStore.set(DEFAULT_HARNESS_PREFERENCE_KEY, harness.id);
}

async function toggleSetting(setting: HarnessSetting): Promise<void> {
  if (savingSetting.value) return;

  savingSetting.value = setting.key;
  settingError.value = null;

  try {
    await prefsStore.set(setting.key, isSettingOn(setting) ? "false" : "true");
    // GitHub Copilot's HydraFusion switch changes the models it offers: the composer asks again at once.
    if (setting.key.startsWith("copilot.")) forgetHarnessCatalogs("copilot");
    refreshAllHarnesses();
  } catch (error) {
    settingError.value = {
      key: setting.key,
      message: error instanceof Error ? error.message : `Fleet couldn't change ${setting.label}.`,
    };
  } finally {
    savingSetting.value = null;
  }
}

function toHarnessCard(harness: HarnessInfo): HarnessCard {
  const metadata = harnessDisplay(harness);
  // The saved switch, as soon as it's flipped; without one, what the server says (on until turned off).
  const enabled = prefsStore.get(`${harness.type}.enabled`, harness.userEnabled ? "true" : "false") === "true";

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
    offersSignIn: harness.capabilities?.supportsProviderSignIn === true && (harness.available || harness.state === "sign-in-required"),
    describesInstall: Boolean(harness.setup?.mode),
    settings: harness.settings ?? [],
    info: harness,
    supportsPermissionLevels: harness.capabilities?.supportsPermissionLevels === true,
    supportsBrowser: harness.capabilities?.supportsAgentBrowser === true,
  };
}

function summaryForHarness(harness: HarnessInfo, enabled: boolean): string {
  if (!enabled) return "Disabled until enabled by the user.";
  if (!harness.available) return harness.reason ?? `${harness.displayName} is registered but not currently available.`;

  // The version is weave-copilot's, the program Fleet runs for GitHub Copilot, and the CLI it drives has its own.
  if (harness.type === "copilot" && harness.version) {
    const cli = harness.cliVersion ? ` · Copilot CLI ${harness.cliVersion}` : "";
    return `weave-copilot ${harness.version}${cli} is installed and ready.`;
  }
  return `${harness.displayName} is installed and ready.`;
}


function statusForHarness(harness: HarnessInfo, enabled: boolean): HarnessStatus {
  return enabled ? harnessState(harness) : "disabled";
}

</script>

<template>
  <div class="grid gap-6">
    <SettingGroup
      id="default-harness"
      title="Default harness"
      hint="Used when a new session doesn't pick one."
    >
      <SettingRow
        id="default-harness"
        title="Default harness"
        :summary="checkedLabel ? undefined : 'The harness a new session starts on.'"
      >
        <template #below>
          <p
            v-if="checkedLabel"
            class="mt-0.5 text-xs text-muted"
            data-testid="harnesses-checked-at"
          >
            {{ isCheckingHarnesses ? "Checking…" : checkedLabel }}
          </p>
        </template>
        <template #control>
          <div class="flex flex-wrap items-center justify-end gap-2">
            <select
              class="h-8 rounded-btn border border-border bg-main-bg px-2 text-xs text-text"
              aria-label="Default harness"
              data-testid="harnesses-default-select"
              :value="defaultHarnessId"
              @change="changeDefault"
            >
              <option
                v-for="harness in harnesses"
                :key="harness.id"
                :value="harness.id"
                :disabled="!harness.enabled || !harness.canDefault"
              >
                {{ harness.name }}
              </option>
            </select>
            <Button
              v-if="!config.cloudMode"
              variant="outline"
              size="sm"
              data-testid="harnesses-set-up"
              @click="harnessSetup.open('harnesses')"
            >
              <Download aria-hidden="true" />
              Set up a harness
            </Button>
            <!-- Check again looks for each harness now, so one installed a moment ago shows up here. -->
            <Button
              variant="outline"
              size="sm"
              :disabled="isCheckingHarnesses"
              data-testid="harnesses-check-again"
              @click="void checkHarnessesAgain()"
            >
              <RefreshCw
                :class="isCheckingHarnesses ? 'animate-spin' : ''"
                aria-hidden="true"
              />
              Check again
            </Button>
          </div>
        </template>
      </SettingRow>
      <SettingRow
        v-if="!config.cloudMode"
        id="harness-update-checks"
        title="Check for updates"
        summary="Fleet looks up each harness's latest version on npm."
      >
        <template #control>
          <SettingSwitch
            :checked="areUpdateChecksOn"
            aria-label="Check for updates"
            data-testid="harnesses-update-checks"
            @click="void toggleUpdateChecks()"
          />
        </template>
      </SettingRow>
    </SettingGroup>

    <div
      class="flex gap-1 overflow-x-auto border-b border-border"
      role="tablist"
      aria-label="Harnesses"
    >
      <button
        v-for="harness in harnesses"
        :key="harness.id"
        type="button"
        role="tab"
        class="-mb-px inline-flex shrink-0 items-center gap-2 border-b-2 px-3 py-2 text-sm"
        :class="selected?.id === harness.id ? 'border-accent text-text' : 'border-transparent text-muted hover:text-text'"
        :aria-selected="selected?.id === harness.id"
        :data-testid="`harness-tab-${harness.id}`"
        @click="selectHarness(harness.id)"
      >
        <span
          class="size-1.5 rounded-full"
          :class="dotClass(harness.status)"
          :title="statusLabel(harness.status)"
          aria-hidden="true"
        />
        {{ harness.name }}
      </button>
    </div>

    <article
      v-if="selected"
      :key="selected.id"
      class="grid gap-6"
      role="tabpanel"
      :data-testid="`harness-panel-${selected.id}`"
    >
      <div class="flex items-start justify-between gap-4">
        <div class="min-w-0">
          <div class="flex flex-wrap items-center gap-2">
            <h2 class="text-lg font-semibold text-text">
              {{ selected.name }}
            </h2>
            <span
              v-if="defaultHarnessId === selected.id"
              class="inline-flex items-center gap-1 rounded-full border border-accent/25 bg-accent/10 px-2 py-0.5 text-[10px] font-medium uppercase text-accent"
            >
              <Star
                :size="11"
                aria-hidden="true"
              />
              Default
            </span>
            <span
              class="inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-[10px] font-medium uppercase"
              :class="statusClasses(selected.status)"
            >
              <component
                :is="statusIcon(selected.status)"
                :size="11"
                aria-hidden="true"
              />
              {{ statusLabel(selected.status) }}
            </span>
          </div>
          <p class="mt-1 text-xs text-muted">
            {{ selected.eyebrow }} · {{ selected.description }}
          </p>
          <p class="mt-1 text-xs text-muted">
            {{ selected.summary }}
          </p>
          <p
            v-if="selected.location"
            class="mt-1 break-all font-mono text-[11px] text-muted"
            data-testid="harness-location"
          >
            {{ selected.location }}
          </p>
        </div>
        <label class="flex shrink-0 items-center gap-2 text-xs font-medium text-muted">
          {{ selected.enabled ? "Enabled" : "Disabled" }}
          <SettingSwitch
            :checked="selected.enabled"
            :disabled="!selected.canToggle"
            :aria-label="`${selected.name} enabled`"
            data-testid="harness-enabled-switch"
            @click="toggleHarness(selected)"
          />
        </label>
      </div>

      <!-- The version and, when there is one, its update; each panel names itself and draws nothing when it has nothing to say. -->
      <div
        v-if="!config.cloudMode"
        data-setting-group="version"
        class="scroll-mt-4 grid gap-2 empty:hidden"
      >
        <HarnessUpdateStrip
          v-if="selected.enabled && !config.cloudMode"
          :harness="selected.info"
        />
        <HarnessInstallPanel
          v-if="selected.describesInstall && !config.cloudMode"
          :harness="selected.info"
          :signs-in-here="selected.offersSignIn"
        />
      </div>

      <!-- Listing providers starts the harness, so one that isn't turned on waits for a click. -->
      <section
        v-if="selected.offersSignIn && !config.cloudMode"
        data-setting-group="providers"
        class="scroll-mt-4 grid gap-2"
      >
        <h3 class="px-1 text-sm font-semibold text-text">
          Providers
        </h3>
        <HarnessSignInPanel
          :harness-type="selected.id"
          :harness-name="selected.name"
          :sign-in-command="selected.info.setup?.signInCommand ?? null"
          :auto-load="selected.enabled"
        />
      </section>

      <SettingGroup
        id="may-do"
        title="What it may do"
      >
        <SettingRow
          v-if="selected.supportsPermissionLevels"
          id="harness-permissions"
          title="Permissions"
          summary="Applies from each session's next message."
        >
          <template #below>
            <p
              class="mt-1 font-mono text-[11.5px] text-muted"
              :data-testid="`permission-handed-${selected.id}`"
            >
              {{ permissions.handedText(selected.info) }}
            </p>
          </template>
          <template #control>
            <select
              :id="`permission-harness-${selected.id}`"
              class="h-8 rounded-btn border border-border bg-main-bg px-2 text-xs text-text"
              :aria-label="`${selected.name} permissions`"
              :value="permissions.ownLevel(selected.id) ?? 'default'"
              @change="permissions.setHarnessLevel(selected.id, ($event.target as HTMLSelectElement).value)"
            >
              <option value="default">
                Default ({{ PERMISSION_LEVEL_NAMES[permissions.defaultLevel.value] }})
              </option>
              <option
                v-for="level in PERMISSION_LEVELS"
                :key="level"
                :value="level"
              >
                {{ PERMISSION_LEVEL_NAMES[level] }}
              </option>
            </select>
          </template>
        </SettingRow>
        <SettingRow
          v-else
          id="harness-permissions"
          title="Permissions"
          :summary="`Fleet can't hand ${selected.name} a permission level yet.`"
        >
          <template #below>
            <span
              class="sr-only"
              :data-testid="`permission-handed-${selected.id}`"
            >Fleet can't hand {{ selected.name }} a permission level yet.</span>
          </template>
          <template #control>
            <span class="text-xs text-muted">Not available</span>
          </template>
        </SettingRow>
        <SettingRow
          id="harness-browser"
          title="Browser"
          :data-testid="`browser-harness-${selected.id}`"
          :summary="selected.supportsBrowser
            ? `${browserSettings.enabled ? 'On' : 'Off'} · ${selected.info.presentation?.agentBrowser ?? 'Fleet\'s browser tools.'}`
            : `Off: Fleet can't add tools to ${selected.name} yet.`"
        >
          <template #control>
            <span class="text-xs text-muted">{{ selected.supportsBrowser && browserSettings.enabled ? "On" : "Off" }}</span>
          </template>
        </SettingRow>
      </SettingGroup>

      <HarnessWeaveGroup
        :harness-type="selected.id"
        :weave="weave"
      />

      <section
        v-if="selected.supportsProfiles"
        data-setting-group="profiles"
        class="scroll-mt-4 grid gap-2"
      >
        <HarnessProfilesPanel
          :harness-type="selected.id"
          :harness-name="selected.name"
          :profile-note="selected.info.presentation?.profileNote ?? null"
        />
      </section>

      <!-- GitHub Copilot runs weave-copilot, which Fleet finds on PATH unless the user names it here. -->
      <HarnessPathField
        v-if="selected.id === 'copilot' && !config.cloudMode"
        :harness="selected.info"
        preference-key="copilot.path"
        program="weave-copilot"
        empty-meaning="use the copy Fleet installed, or weave-copilot on PATH"
      />

      <SettingGroup
        v-if="selected.settings.length > 0"
        id="harness-settings"
        :title="`${selected.name} settings`"
      >
        <SettingRow
          v-for="setting in selected.settings"
          :id="`harness-setting-${setting.key}`"
          :key="setting.key"
          :title="setting.label"
          :summary="`${setting.default ? 'On' : 'Off'} by default. ${setting.description}`"
          :data-testid="`harness-setting-${setting.key}`"
        >
          <template #below>
            <p
              v-if="settingError?.key === setting.key"
              class="mt-2 text-xs text-red-300"
              role="alert"
            >
              {{ settingError.message }}
            </p>
          </template>
          <template #control>
            <SettingSwitch
              :checked="isSettingOn(setting)"
              :disabled="prefsStore.isLoading || savingSetting !== null"
              :saving="savingSetting === setting.key"
              :aria-label="setting.label"
              :data-testid="`harness-setting-toggle-${setting.key}`"
              @click="toggleSetting(setting)"
            />
          </template>
        </SettingRow>
      </SettingGroup>
      <p
        v-else-if="!selected.describesInstall"
        class="px-1 text-xs text-muted"
      >
        No settings yet
      </p>
    </article>
  </div>
</template>
