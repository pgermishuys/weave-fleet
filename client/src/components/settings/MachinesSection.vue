<script setup lang="ts">
import { computed, onMounted, onUnmounted, reactive, shallowRef, watch } from "vue";
import { storeToRefs } from "pinia";
import { AlertCircle, Check, Copy, Eye, EyeOff, LoaderCircle, Plus, RefreshCw } from "lucide-vue-next";
import { useMachinesStore, type MachineAccess, type MachineEntry, type MachineHarness, type PairingPreview } from "@/stores/machines";
import SettingGroup from "@/components/settings/SettingGroup.vue";
import SettingRow from "@/components/settings/SettingRow.vue";
import AddPhonePanel from "@/components/settings/AddPhonePanel.vue";
import MachineAddNote from "@/components/settings/MachineAddNote.vue";
import MachineDiagnosisPanel from "@/components/settings/MachineDiagnosisPanel.vue";
import MachineSetupCard from "@/components/settings/MachineSetupCard.vue";
import MachineJobSeats from "@/components/settings/MachineJobSeats.vue";
import { useJobsFeature } from "@/composables/use-jobs-feature";
import DevicesList from "@/components/settings/DevicesList.vue";
import { listDevices, type PairedDevice } from "@/lib/devices-api";
import { diagnosisOf, tunnelDownDiagnosis, type MachineDiagnosis } from "@/lib/machine-diagnosis";
import { AGENT_HANDOFF_PREFERENCE_KEY } from "@/lib/machines";
import { describeReach, parsePairingInput } from "@/lib/pairing-link";
import { normalizeManualCode } from "@/lib/pairing";
import { SESSION_MESSAGES_PREFERENCE_KEY } from "@/lib/session-messages";
import { useSettingsNav } from "@/composables/use-settings-nav";
import { usePreferencesStore } from "@/stores/preferences";

/**
 * Settings → Machines: the machines this client knows, adding one by URL and token, and how other devices reach
 * this one. The list belongs to this client (it lives in this browser or desktop app); each machine's name
 * belongs to the machine, so a rename shows up on every client.
 */

const buttonPrimaryClass = "inline-flex items-center justify-center gap-2 rounded-btn bg-primary px-3 py-2 text-sm font-medium text-white transition-opacity hover:opacity-90 disabled:cursor-not-allowed disabled:opacity-60";
const buttonSecondaryClass = "inline-flex items-center justify-center gap-2 rounded-btn border border-border bg-main-bg px-3 py-1.5 text-sm font-medium text-text transition-colors hover:border-accent/50 disabled:cursor-not-allowed disabled:opacity-60";
const buttonDangerClass = "inline-flex items-center justify-center gap-2 rounded-btn border border-border bg-main-bg px-3 py-1.5 text-sm font-medium text-text transition-colors hover:border-red-500/60 hover:text-red-400 disabled:cursor-not-allowed disabled:opacity-60";
const inputClass = "w-full rounded-btn border border-border bg-main-bg px-3 py-2 text-sm text-text outline-none transition-colors placeholder:text-muted focus:border-accent";

const machines = useMachinesStore();
const { entries, others, home, liveReachable, source, candidates } = storeToRefs(machines);
const preferencesStore = usePreferencesStore();
preferencesStore.ensureLoaded();
// Each card draws MachineJobSeats from a list that is empty with Jobs off, not under a v-if: a false v-if still leaves a
// comment node, and with Jobs off the card must render exactly as it did.
const { isJobsEnabled: jobsOn } = useJobsFeature();

// ── Add ──────────────────────────────────────────────────────────────────────
const showAddForm = shallowRef(false);
const addForm = reactive({ link: "", token: "", code: "" });
type AddMode = "setup" | "link";
const addModes: AddMode[] = ["setup", "link"];
const modeLabels: Record<AddMode, string> = { setup: "Set it up from here", link: "Paste a pairing link" };
const addMode = shallowRef<AddMode>("link");
/** Set when a machine can't be set up from here: the link card says which machine and what to run on it. */
const linkHint = shallowRef<string | null>(null);
/** A set-up is running in the card: leaving the dialog doesn't stop it, so the button says Close. */
const setupRunning = shallowRef(false);
function pickMode(mode: AddMode): void {
  addMode.value = mode;
  setupRunning.value = false;
  addError.value = null;
  linkHint.value = null;
}
async function openAddForm(): Promise<void> {
  setupRunning.value = false;
  showAddForm.value = true;
  addError.value = null;
  linkHint.value = null;
  justAdded.value = null;
  addMode.value = "link";
  await machines.loadCandidates();
  if (showAddForm.value && (candidates.value?.length ?? 0) > 0) addMode.value = "setup";
}
function onSetUp(name: string): void {
  justAdded.value = name;
  showAddForm.value = false;
}
function onUseLink(name: string): void {
  addMode.value = "link";
  linkHint.value = name;
}
const showAdvanced = shallowRef(false);
const isAdding = shallowRef(false);
const addError = shallowRef<string | null>(null);
const addDiagnosis = shallowRef<MachineDiagnosis | null>(null);
const justAdded = shallowRef<string | null>(null);

// Sent here by the composer chip, the sessions list or the command palette: open the form once, as if clicked.
const { addMachineRequested } = useSettingsNav();
watch(addMachineRequested, (requested) => {
  if (!requested) return;
  addMachineRequested.value = false;
  void openAddForm();
}, { immediate: true });

// What the Pairing link field holds: a whole link, or an address that still needs its code.
const pasted = computed(() => parsePairingInput(addForm.link));
const pairTarget = computed(() => {
  const input = pasted.value;
  if (input?.kind === "link") return { baseUrl: input.baseUrl, code: input.code };
  const code = input?.kind === "address" ? normalizeManualCode(addForm.code) : null;
  return input?.kind === "address" && code ? { baseUrl: input.baseUrl, code } : null;
});
/** With the master token typed under Advanced there is no code to look at; Fleet checks the token when it adds. */
const usesToken = computed(() => pasted.value?.kind === "address" && addForm.token.trim().length > 0);

/** The machine behind the pasted link, once Fleet has looked at it; `previewError` says why not. */
const found = shallowRef<PairingPreview | null>(null);
const previewError = shallowRef<string | null>(null);
const previewDiagnosis = shallowRef<MachineDiagnosis | null>(null);
const isLooking = shallowRef(false);
const allowAgents = shallowRef(false);
let lookToken = 0;

watch(pairTarget, async (target) => {
  const mine = ++lookToken;
  found.value = null;
  previewError.value = null;
  previewDiagnosis.value = null;
  allowAgents.value = false;
  isLooking.value = target !== null;
  if (!target) return;
  try {
    const preview = await machines.previewPairing(target.baseUrl, target.code);
    if (mine === lookToken) found.value = preview;
  } catch (error) {
    if (mine === lookToken) {
      previewDiagnosis.value = diagnosisOf(error);
      previewError.value = previewDiagnosis.value?.headline ?? (error instanceof Error ? error.message : String(error));
    }
  } finally {
    if (mine === lookToken) isLooking.value = false;
  }
}, { immediate: true });

const showsAllowAgents = computed(() => found.value !== null && isAgentHandoffOn.value && source.value === "server");
const foundLine = computed(() => found.value
  ? [found.value.name, found.value.os ? osLabel(found.value.os) : null, found.value.version ? `Fleet ${found.value.version}` : null].filter(Boolean).join(" · ")
  : "");
const foundReach = computed(() => found.value ? `Reached ${describeReach(found.value.baseUrl)}` : "");
const addLabel = computed(() => found.value && !usesToken.value ? `Add ${found.value.name}` : "Add machine");
const canAdd = computed(() => usesToken.value || (found.value !== null && pairTarget.value !== null));

function osLabel(os: string): string {
  return ({ linux: "Linux", macos: "macOS", windows: "Windows" } as Record<string, string>)[os.toLowerCase()] ?? os;
}

function clearAddForm(): void {
  addForm.link = "";
  addForm.token = "";
  addForm.code = "";
}

async function addMachine(): Promise<void> {
  isAdding.value = true;
  addError.value = null;
  addDiagnosis.value = null;
  try {
    const target = pairTarget.value;
    const input = pasted.value;
    const machine = usesToken.value && input?.kind === "address"
      ? await machines.addMachine(input.baseUrl, addForm.token)
      : await machines.pairMachine(target!.baseUrl, target!.code);
    if (allowAgents.value && showsAllowAgents.value)
      await machines.setAgentsAllowed(machine.id, true).catch(() => {
        // The machine is added; the switch stays off and can be set on its card.
      });
    justAdded.value = machine.name;
    clearAddForm();
    showAddForm.value = false;
  } catch (error) {
    addDiagnosis.value = diagnosisOf(error);
    addError.value = addDiagnosis.value?.headline ?? (error instanceof Error ? error.message : String(error));
  } finally {
    isAdding.value = false;
  }
}

// ── Each machine ─────────────────────────────────────────────────────────────
const renaming = shallowRef<string | null>(null);
const renameValue = shallowRef("");
const tokenFor = shallowRef<string | null>(null);
const tokenValue = shallowRef("");
const busyKey = shallowRef<string | null>(null);
const rowError = reactive<Record<string, string | null>>({});

function startRename(entry: MachineEntry): void {
  renaming.value = entry.key;
  renameValue.value = entry.name;
  rowError[entry.key] = null;
}

async function saveRename(entry: MachineEntry): Promise<void> {
  busyKey.value = entry.key;
  try {
    await machines.renameMachine(entry.key, renameValue.value.trim());
    renaming.value = null;
  } catch (error) {
    rowError[entry.key] = error instanceof Error ? error.message : String(error);
  } finally {
    busyKey.value = null;
  }
}

function startToken(entry: MachineEntry): void {
  tokenFor.value = entry.key;
  tokenValue.value = "";
  rowError[entry.key] = null;
}

async function saveToken(entry: MachineEntry): Promise<void> {
  busyKey.value = entry.key;
  try {
    await machines.updateToken(entry.key, tokenValue.value);
    tokenFor.value = null;
  } catch (error) {
    rowError[entry.key] = error instanceof Error ? error.message : String(error);
  } finally {
    busyKey.value = null;
  }
}

// ── Agent hand-off ───────────────────────────────────────────────────────────
// Shown with "Hand work to other machines" on, for the machines in home's own list (only its owner sees that list).
const isAgentHandoffOn = computed(() =>
  preferencesStore.get(AGENT_HANDOFF_PREFERENCE_KEY, "false") === "true"
  && preferencesStore.get(SESSION_MESSAGES_PREFERENCE_KEY, "false") === "true");

function showsAgentsAllowed(entry: MachineEntry): boolean {
  return isAgentHandoffOn.value && !entry.isHome && source.value === "server";
}

async function toggleAgentsAllowed(entry: MachineEntry): Promise<void> {
  busyKey.value = entry.key;
  rowError[entry.key] = null;
  try {
    await machines.setAgentsAllowed(entry.key, machines.agentsAllowed[entry.key] !== true);
  } catch (error) {
    rowError[entry.key] = error instanceof Error ? error.message : String(error);
  } finally {
    busyKey.value = null;
  }
}

function forget(entry: MachineEntry): void {
  if (!window.confirm(`Forget ${entry.name}? Its sessions stay on ${entry.name}; this device just stops listing them.`)) return;
  machines.forgetMachine(entry.key);
}

/** Why a machine isn't answering, when known: its tunnel being down, or what home or this page found. */
function diagnosisFor(entry: MachineEntry): MachineDiagnosis | null {
  return entry.tunnelUp === false ? tunnelDownDiagnosis(entry.name, entry.sshDestination ?? null) : machines.diagnoses[entry.key] ?? null;
}

function stateLine(entry: MachineEntry): { text: string; tone: "ok" | "bad" | "muted" } {
  if (entry.tunnelUp === false) return { text: diagnosisFor(entry)!.headline, tone: "bad" };
  if (entry.isLive) return { text: "live", tone: "ok" };
  const state = others.value[entry.key];
  if (state?.error) return { text: diagnosisFor(entry)?.headline ?? state.error, tone: "bad" };
  if (state?.loadedAt) return { text: `reachable · ${state.sessions.length} ${state.sessions.length === 1 ? "session" : "sessions"}`, tone: "ok" };
  return { text: "checking…", tone: "muted" };
}

/**
 * What the machine last said it can run (its ready harnesses) and how busy it is. Nothing for a Fleet too old to say,
 * or one that isn't answering now: its last numbers would be out of date.
 */
function capabilityLine(entry: MachineEntry): string | null {
  const capabilities = entry.capabilities;
  const answering = entry.isLive ? entry.isHome || liveReachable.value : !others.value[entry.key]?.error;
  if (!capabilities || !answering) return null;

  const parts: string[] = [];
  if (capabilities.harnesses) {
    const ready = readyHarnesses(entry).map((harness) => harness.name);
    parts.push(ready.length ? `Ready: ${ready.join(", ")}` : "No harness ready");
  }
  const { working, needsYou } = capabilities.sessions;
  parts.push(`${working} working`);
  if (needsYou) parts.push(`${needsYou} ${needsYou === 1 ? "needs" : "need"} you`);
  return parts.join(" · ");
}

function readyHarnesses(entry: MachineEntry): MachineHarness[] {
  return entry.capabilities?.harnesses?.filter((harness) => harness.available && harness.enabled) ?? [];
}

function harnessVersions(entry: MachineEntry): string | undefined {
  const versions = readyHarnesses(entry).map((harness) => harness.version ? `${harness.name} ${harness.version}` : harness.name);
  return versions.length ? versions.join(", ") : undefined;
}

function location(entry: MachineEntry): string {
  return entry.isHome ? "this machine" : entry.baseUrl;
}

// ── This machine's access ────────────────────────────────────────────────────
const access = shallowRef<MachineAccess | null>(null);
/** This browser signed in as a paired device: it may not see the token or manage devices. */
const isDevice = shallowRef(false);
const accessLoaded = shallowRef(false);
const accessError = shallowRef<string | null>(null);
const showToken = shallowRef(false);
const copied = shallowRef<string | null>(null);
const isReplacing = shallowRef(false);

const homeName = computed(() => home.value?.name ?? "This machine");

async function loadAccess(): Promise<void> {
  accessError.value = null;
  try {
    const loaded = await machines.loadHomeAccess();
    isDevice.value = loaded === "device";
    access.value = loaded === "device" ? null : loaded;
    if (access.value) void refreshDevices();
  } catch (error) {
    accessError.value = error instanceof Error ? error.message : String(error);
  } finally {
    accessLoaded.value = true;
  }
}

// ── Paired devices ───────────────────────────────────────────────────────────
const devices = shallowRef<PairedDevice[]>([]);
const deviceNames = computed(() => devices.value.map((device) => device.name));
const thisComputer = computed(() => access.value?.requiresToken
  ? "Signed in with the machine token"
  : "Signed in on loopback");

async function refreshDevices(): Promise<void> {
  try {
    devices.value = await listDevices();
  } catch {
    // An older Fleet has no devices; the list just stays empty.
  }
}

function onPublicUrlSaved(url: string): void {
  if (home.value) home.value = { ...home.value, publicUrl: url || null };
}

async function copy(value: string, what: string): Promise<void> {
  try {
    await navigator.clipboard.writeText(value);
    copied.value = what;
    setTimeout(() => {
      if (copied.value === what) copied.value = null;
    }, 1500);
  } catch {
    // No clipboard (insecure origin): the value is on screen to select.
  }
}

async function replaceToken(): Promise<void> {
  if (!window.confirm("Replace this machine's token? Every device using the old one loses access until you give it the new one.")) return;
  isReplacing.value = true;
  accessError.value = null;
  try {
    access.value = await machines.replaceHomeToken();
    showToken.value = true;
  } catch (error) {
    accessError.value = error instanceof Error ? error.message : String(error);
  } finally {
    isReplacing.value = false;
  }
}

let stopPolling: (() => void) | null = null;
onMounted(() => {
  void machines.loadHome();
  void loadAccess();
  stopPolling = machines.startPolling();
});
onUnmounted(() => stopPolling?.());
</script>

<template>
  <div
    class="grid gap-6"
    data-testid="machines-section"
  >
    <SettingGroup
      id="machines"
      title="Machines"
    >
      <SettingRow
        id="add-machine"
        title="Add a machine"
        summary="Every machine runs its own Fleet and keeps its own sessions, repositories and sign-ins. A session stays on the machine it started on. The sessions list shows them all; the one you're working in is live."
      >
        <template #control>
          <button
            v-if="!showAddForm"
            type="button"
            :class="buttonPrimaryClass"
            data-testid="machines-add"
            @click="openAddForm()"
          >
            <Plus
              :size="16"
              aria-hidden="true"
            />
            Add a machine
          </button>
        </template>
      </SettingRow>

      <form
        v-if="showAddForm"
        class="grid gap-3 bg-main-bg px-4 py-4"
        data-testid="machines-add-form"
        @submit.prevent="addMachine"
      >
        <div>
          <h3 class="text-sm font-semibold text-text">
            Add a machine
          </h3>
        </div>
        <div
          class="flex gap-1 rounded-btn border border-border bg-main-bg p-1 text-sm"
          role="tablist"
          aria-label="How to add the machine"
        >
          <button
            v-for="mode in addModes"
            :key="mode"
            type="button"
            role="tab"
            :aria-selected="addMode === mode"
            class="flex-1 rounded-btn px-3 py-1.5 font-medium transition-colors"
            :class="addMode === mode ? 'bg-accent/15 text-text' : 'text-muted hover:text-text'"
            :data-testid="`machines-add-mode-${mode}`"
            @click="pickMode(mode)"
          >
            {{ modeLabels[mode] }}
          </button>
        </div>
        <MachineSetupCard
          v-if="addMode === 'setup'"
          @added="onSetUp"
          @use-link="onUseLink"
          @running="setupRunning = $event"
        />
        <template v-else>
          <p
            v-if="linkHint"
            class="text-xs text-text"
            data-testid="machines-add-link-hint"
          >
            Fleet can't set up {{ linkHint }} from here. On {{ linkHint }}, run <code class="machine-code">fleet node up</code> and paste the link it prints.
          </p>
          <p
            class="text-xs text-muted"
            data-testid="machines-add-link-help"
          >
            On the other machine, run <code class="machine-code">fleet node pair</code>. It prints a pairing link that
            works once for 10 minutes. Paste it here.
          </p>
          <label class="grid gap-1 text-sm text-text">
            <span class="text-xs font-medium uppercase tracking-wide text-muted">Pairing link</span>
            <input
              v-model="addForm.link"
              type="text"
              inputmode="url"
              autocomplete="off"
              spellcheck="false"
              :class="inputClass"
              placeholder="http://100.64.90.72:2113/add-machine#ABCD-EFGH"
              :disabled="isAdding"
              data-testid="machines-add-link"
            >
            <span class="text-xs text-muted">Or just the machine's address, then its code.</span>
          </label>
          <p
            v-if="pasted?.kind === 'invalid'"
            class="text-xs text-red-300"
            data-testid="machines-add-link-invalid"
          >
            {{ pasted.message }}
          </p>
          <template v-else-if="pasted?.kind === 'address'">
            <label
              v-if="!usesToken"
              class="grid gap-1 text-sm text-text"
            >
              <span class="text-xs font-medium uppercase tracking-wide text-muted">Pairing code</span>
              <input
                v-model="addForm.code"
                type="text"
                autocomplete="off"
                autocapitalize="characters"
                spellcheck="false"
                :class="[inputClass, 'font-mono tracking-widest']"
                placeholder="ABCD-EFGH"
                :disabled="isAdding"
                data-testid="machines-add-code"
              >
            </label>
            <button
              v-if="!showAdvanced"
              type="button"
              class="justify-self-start text-xs text-muted underline-offset-2 hover:text-text hover:underline"
              data-testid="machines-add-advanced"
              @click="showAdvanced = true"
            >
              Advanced: use the machine's access token
            </button>
            <label
              v-else
              class="grid gap-1 text-sm text-text"
            >
              <span class="text-xs font-medium uppercase tracking-wide text-muted">Access token</span>
              <input
                v-model="addForm.token"
                type="password"
                autocomplete="off"
                spellcheck="false"
                :class="inputClass"
                placeholder="Paste the machine's token"
                :disabled="isAdding"
                data-testid="machines-add-token"
              >
              <span class="text-xs text-muted">This is the machine's full-access token. A pairing link is safer.</span>
            </label>
          </template>
          <p
            v-if="isLooking"
            class="flex items-center gap-2 text-xs text-muted"
            data-testid="machines-add-looking"
          >
            <LoaderCircle
              :size="14"
              class="animate-spin"
              aria-hidden="true"
            />
            Looking for the machine…
          </p>
          <div
            v-if="previewError"
            class="flex items-start gap-2 rounded-card border border-red-500/30 bg-red-500/10 px-3 py-2 text-sm text-red-300"
            role="alert"
            data-testid="machines-add-preview-error"
          >
            <AlertCircle
              :size="16"
              class="mt-0.5 shrink-0"
              aria-hidden="true"
            />
            <div>
              <span>{{ previewError }}</span>
              <MachineDiagnosisPanel
                v-if="previewDiagnosis"
                :diagnosis="previewDiagnosis"
              />
            </div>
          </div>
          <div
            v-if="found && !usesToken"
            class="grid gap-3 rounded-card border border-border bg-card-bg p-3"
            data-testid="machines-add-found"
          >
            <div>
              <p
                class="flex items-center gap-2 text-sm font-medium text-text"
                data-testid="machines-add-found-line"
              >
                <Check
                  :size="16"
                  class="text-[var(--running)]"
                  aria-hidden="true"
                />
                {{ foundLine }}
              </p>
              <p
                class="mt-0.5 pl-6 text-xs text-muted"
                data-testid="machines-add-found-reach"
              >
                {{ foundReach }}
              </p>
            </div>
            <MachineAddNote :name="found.name" />
            <label
              v-if="showsAllowAgents"
              class="flex items-center gap-2 text-sm text-text"
            >
              <input
                v-model="allowAgents"
                type="checkbox"
                :disabled="isAdding"
                data-testid="machines-add-allow-agents"
              >
              Agents can hand work to {{ found.name }}
            </label>
          </div>
        </template>
        <div
          v-if="addError"
          class="flex items-start gap-2 rounded-card border border-red-500/30 bg-red-500/10 px-3 py-2 text-sm text-red-300"
          role="alert"
          data-testid="machines-add-error"
        >
          <AlertCircle
            :size="16"
            class="mt-0.5 shrink-0"
            aria-hidden="true"
          />
          <div>
            <span>{{ addError }}</span>
            <MachineDiagnosisPanel
              v-if="addDiagnosis"
              :diagnosis="addDiagnosis"
            />
          </div>
        </div>
        <div class="flex flex-wrap gap-2">
          <button
            v-if="addMode === 'link'"
            type="submit"
            :class="buttonPrimaryClass"
            :disabled="isAdding || !canAdd"
            data-testid="machines-add-submit"
          >
            <LoaderCircle
              v-if="isAdding"
              :size="16"
              class="animate-spin"
              aria-hidden="true"
            />
            {{ isAdding ? "Checking…" : addLabel }}
          </button>
          <button
            type="button"
            :class="buttonSecondaryClass"
            :disabled="isAdding"
            data-testid="machines-add-cancel"
            @click="showAddForm = false"
          >
            {{ addMode === "setup" && setupRunning ? "Close" : "Cancel" }}
          </button>
        </div>
      </form>

      <p
        v-if="justAdded"
        class="flex items-center gap-2 px-4 py-3 text-sm text-text"
        role="status"
      >
        <Check
          :size="16"
          class="text-[var(--running)]"
          aria-hidden="true"
        />
        Added {{ justAdded }}. Its sessions are in the sessions list.
      </p>

      <ul
        class="divide-y divide-border"
        aria-label="Machines"
      >
        <li
          v-for="entry in entries"
          :key="entry.key"
          class="machine-card"
          data-testid="machine-card"
          :data-machine="entry.key"
        >
          <span
            class="machine-card__dot"
            :class="`machine-card__dot--${stateLine(entry).tone}`"
            aria-hidden="true"
          />
          <div class="machine-card__names">
            <template v-if="renaming === entry.key">
              <form
                class="flex flex-wrap items-center gap-2"
                @submit.prevent="saveRename(entry)"
              >
                <input
                  v-model="renameValue"
                  type="text"
                  maxlength="64"
                  :class="inputClass"
                  class="max-w-[240px] py-1"
                  :aria-label="`New name for ${entry.name}`"
                  data-testid="machine-rename-input"
                >
                <button
                  type="submit"
                  :class="buttonSecondaryClass"
                  :disabled="busyKey === entry.key"
                >
                  Save
                </button>
                <button
                  type="button"
                  :class="buttonSecondaryClass"
                  @click="renaming = null"
                >
                  Cancel
                </button>
              </form>
            </template>
            <span
              v-else
              class="machine-card__name"
            >
              {{ entry.name }}
              <span
                v-if="entry.os"
                class="machine-card__os"
              >{{ entry.os }}</span>
              <span
                v-if="entry.isLive"
                class="machine-card__live"
              >Live</span>
            </span>
            <span
              v-if="entry.sshDestination"
              class="machine-card__url"
              data-testid="machine-ssh-line"
            >Over SSH as {{ entry.sshDestination }}</span>
            <span
              v-else
              class="machine-card__url"
            >{{ location(entry) }}</span>
            <span
              class="machine-card__state"
              :class="`machine-card__state--${stateLine(entry).tone}`"
              :data-testid="entry.tunnelUp === false ? 'machine-tunnel-down' : undefined"
            >{{ stateLine(entry).text }}</span>
            <span
              v-if="capabilityLine(entry)"
              class="machine-card__capabilities"
              :title="harnessVersions(entry)"
              data-testid="machine-capabilities"
            >{{ capabilityLine(entry) }}</span>
            <label
              v-if="showsAgentsAllowed(entry)"
              class="machine-card__agents"
              data-testid="machine-agents-allowed"
            >
              <input
                type="checkbox"
                :checked="machines.agentsAllowed[entry.key] === true"
                :disabled="busyKey === entry.key"
                @change="toggleAgentsAllowed(entry)"
              >
              Agents can hand work here
            </label>
            <MachineJobSeats
              v-for="key in jobsOn ? [entry.key] : []"
              :key="key"
              :entry-key="key"
            />

            <form
              v-if="tokenFor === entry.key"
              class="mt-2 flex flex-wrap items-center gap-2"
              @submit.prevent="saveToken(entry)"
            >
              <input
                v-model="tokenValue"
                type="password"
                autocomplete="off"
                :class="inputClass"
                class="max-w-[320px] py-1"
                placeholder="Paste the new token"
                :aria-label="`New token for ${entry.name}`"
              >
              <button
                type="submit"
                :class="buttonSecondaryClass"
                :disabled="busyKey === entry.key || !tokenValue.trim()"
              >
                Save
              </button>
              <button
                type="button"
                :class="buttonSecondaryClass"
                @click="tokenFor = null"
              >
                Cancel
              </button>
            </form>
            <details
              v-if="diagnosisFor(entry)"
              class="mt-1 text-xs"
              data-testid="machine-details"
            >
              <summary class="cursor-pointer text-text-muted">
                Details
              </summary>
              <MachineDiagnosisPanel
                :diagnosis="diagnosisFor(entry)!"
                retryable
                @retry="machines.retryMachine(entry.key)"
              />
            </details>
            <p
              v-if="rowError[entry.key]"
              class="mt-1 text-xs text-red-400"
              role="alert"
            >
              {{ rowError[entry.key] }}
            </p>
          </div>

          <div
            v-if="renaming !== entry.key"
            class="machine-card__actions"
          >
            <button
              v-if="!entry.isLive"
              type="button"
              :class="buttonSecondaryClass"
              data-testid="machine-open"
              @click="machines.openOn(entry.key, '/')"
            >
              Work here
            </button>
            <button
              type="button"
              :class="buttonSecondaryClass"
              @click="startRename(entry)"
            >
              Rename
            </button>
            <button
              v-if="!entry.isHome"
              type="button"
              :class="buttonSecondaryClass"
              @click="startToken(entry)"
            >
              New token
            </button>
            <button
              v-if="!entry.isHome"
              type="button"
              :class="buttonDangerClass"
              data-testid="machine-forget"
              @click="forget(entry)"
            >
              Forget
            </button>
          </div>
        </li>
      </ul>
    </SettingGroup>

    <SettingGroup
      id="phones"
      :title="`Let other devices reach ${homeName}`"
      hint="Phones and devices, and the access token."
      data-testid="machine-access"
    >
      <p
        v-if="accessError"
        class="px-4 py-3 text-sm text-red-400"
        role="alert"
      >
        {{ accessError }}
      </p>

      <p
        v-else-if="!access"
        class="px-4 py-3 text-sm text-muted"
      >
        <template v-if="isDevice">
          This browser is signed in as a paired device. Adding phones and seeing the token happen on the computer.
        </template>
        <template v-else>
          {{ accessLoaded ? "This Fleet doesn't hand out access tokens: it signs people in with an identity provider, or it's too old for machines." : "Loading…" }}
        </template>
      </p>

      <template v-else>
        <div
          class="px-4 py-3"
          data-testid="machine-phones"
        >
          <AddPhonePanel
            :machine-name="homeName"
            :public-url="home?.publicUrl ?? null"
            :addresses="access.addresses"
            :requires-token="access.requiresToken"
            :port="access.port"
            :device-names="deviceNames"
            @refresh="refreshDevices"
            @saved-url="onPublicUrlSaved"
          />
          <DevicesList
            :devices="devices"
            :this-computer="thisComputer"
            @removed="refreshDevices"
          />
        </div>

        <p
          v-if="!access.remoteReachable && !access.requiresToken"
          class="max-w-prose px-4 py-3 text-sm text-muted"
          data-testid="machine-access-loopback"
        >
          Only this computer can reach it: Fleet listens on {{ access.host }}:{{ access.port }}. To add it from
          another device, start Fleet with <code class="machine-code">--host 0.0.0.0</code> (or your tailnet
          address), or put <code class="machine-code">tailscale serve</code> in front of it and start Fleet with
          <code class="machine-code">--require-token</code>.
        </p>
        <p
          v-else-if="!access.remoteReachable"
          class="max-w-prose px-4 py-3 text-sm text-muted"
        >
          Fleet listens on {{ access.host }}:{{ access.port }} and asks every request for the token, so a proxy
          such as <code class="machine-code">tailscale serve</code> can forward other devices to it. Use the
          proxy's address.
        </p>

        <div
          v-if="access.addresses.length"
          class="grid gap-1 px-4 py-3"
        >
          <span class="text-xs font-medium uppercase tracking-wide text-muted">Addresses</span>
          <div
            v-for="address in access.addresses"
            :key="address.url"
            class="flex flex-wrap items-center gap-2"
          >
            <code class="machine-value">{{ address.url }}</code>
            <span class="machine-card__os">{{ address.kind }}</span>
            <button
              type="button"
              class="machine-icon-button"
              :aria-label="`Copy ${address.url}`"
              @click="copy(address.url, address.url)"
            >
              <Check
                v-if="copied === address.url"
                :size="14"
                aria-hidden="true"
              />
              <Copy
                v-else
                :size="14"
                aria-hidden="true"
              />
            </button>
          </div>
        </div>

        <div class="grid gap-1 px-4 py-3">
          <span class="text-xs font-medium uppercase tracking-wide text-muted">Access token</span>
          <div class="flex flex-wrap items-center gap-2">
            <code
              class="machine-value"
              data-testid="machine-access-token"
            >{{ showToken ? access.token : "•".repeat(24) }}</code>
            <button
              type="button"
              class="machine-icon-button"
              :aria-label="showToken ? 'Hide token' : 'Show token'"
              @click="showToken = !showToken"
            >
              <EyeOff
                v-if="showToken"
                :size="14"
                aria-hidden="true"
              />
              <Eye
                v-else
                :size="14"
                aria-hidden="true"
              />
            </button>
            <button
              type="button"
              class="machine-icon-button"
              aria-label="Copy token"
              @click="copy(access.token, 'token')"
            >
              <Check
                v-if="copied === 'token'"
                :size="14"
                aria-hidden="true"
              />
              <Copy
                v-else
                :size="14"
                aria-hidden="true"
              />
            </button>
            <button
              v-if="access.tokenSource !== 'environment'"
              type="button"
              :class="buttonSecondaryClass"
              :disabled="isReplacing"
              @click="replaceToken"
            >
              <RefreshCw
                :size="14"
                aria-hidden="true"
              />
              Replace token
            </button>
          </div>
          <p class="mt-1 max-w-prose text-xs text-muted">
            <template v-if="access.tokenSource === 'environment'">
              WEAVE_FLEET_AUTH_TOKEN sets it; change the variable and restart Fleet to replace it.
            </template>
            <template v-else>
              Anyone who has it can run agents, change files and open terminals on this machine. Replacing it locks out every device that
              has the old one.
            </template>
          </p>
        </div>
      </template>
    </SettingGroup>
  </div>
</template>

<style scoped>
.machine-card {
  display: flex;
  flex-wrap: wrap;
  align-items: flex-start;
  gap: 12px;
  padding: 12px 16px;
}

.machine-card__dot {
  width: 8px;
  height: 8px;
  margin-top: 6px;
  flex-shrink: 0;
  border-radius: 50%;
  background: var(--muted);
}

.machine-card__dot--ok {
  background: var(--running);
}

.machine-card__dot--bad {
  background: var(--error);
}

.machine-card__names {
  display: flex;
  flex: 1 1 220px;
  min-width: 0;
  flex-direction: column;
  gap: 2px;
}

.machine-card__name {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 7px;
  font-family: var(--font-mono);
  font-size: 13.5px;
  font-weight: 600;
  color: var(--text);
}

.machine-card__os {
  padding: 0 5px;
  border: 1px solid var(--border);
  border-radius: 4px;
  font-family: var(--font-mono);
  font-size: 10px;
  font-weight: 600;
  letter-spacing: 0.05em;
  text-transform: uppercase;
  color: var(--muted);
}

.machine-card__live {
  padding: 1px 5px;
  border-radius: 4px;
  background: color-mix(in srgb, var(--coral) 14%, transparent);
  color: var(--coral);
  font-size: 9.5px;
  font-weight: 700;
  letter-spacing: 0.09em;
  text-transform: uppercase;
}

.machine-card__url {
  overflow-wrap: anywhere;
  font-family: var(--font-mono);
  font-size: 11.5px;
  color: var(--muted);
}

.machine-card__state {
  font-family: var(--font-mono);
  font-size: 11.5px;
  color: var(--muted);
}

.machine-card__capabilities {
  font-family: var(--font-mono);
  font-size: 11.5px;
  color: var(--muted);
}

.machine-card__agents {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  margin-top: 4px;
  font-size: 12.5px;
  color: var(--text);
  cursor: pointer;
}

.machine-card__state--ok {
  color: var(--running);
}

.machine-card__state--bad {
  color: var(--error);
}

.machine-card__actions {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}

.machine-code {
  padding: 1px 5px;
  border-radius: 4px;
  background: var(--accent-dim);
  font-family: var(--font-mono);
  font-size: 0.85em;
}

.machine-value {
  overflow-wrap: anywhere;
  padding: 5px 9px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--main-bg);
  font-family: var(--font-mono);
  font-size: 12px;
  color: var(--text);
}

.machine-icon-button {
  display: grid;
  place-items: center;
  width: 28px;
  height: 28px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: transparent;
  color: var(--muted);
  cursor: pointer;
  transition: border-color var(--transition), color var(--transition);
}

.machine-icon-button:hover {
  border-color: var(--accent);
  color: var(--text);
}
</style>
