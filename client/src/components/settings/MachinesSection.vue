<script setup lang="ts">
import { computed, onMounted, onUnmounted, reactive, shallowRef, watch } from "vue";
import { storeToRefs } from "pinia";
import { useRouter } from "@tanstack/vue-router";
import { Check, Plus } from "lucide-vue-next";
import {
  useMachinesStore,
  type PairingPreview,
} from "@/stores/machines";
import SettingGroup from "@/components/settings/SettingGroup.vue";
import SettingRow from "@/components/settings/SettingRow.vue";
import AddPhonePanel from "@/components/settings/AddPhonePanel.vue";
import MachineAddNote from "@/components/settings/MachineAddNote.vue";
import MachineDiagnosisPanel from "@/components/settings/MachineDiagnosisPanel.vue";
import FoundMachinesCard from "@/components/settings/FoundMachinesCard.vue";
import MachineSetupCard from "@/components/settings/MachineSetupCard.vue";
import MachinePage from "@/components/settings/MachinePage.vue";
import NewServerCard from "@/components/settings/NewServerCard.vue";
import DevicesList from "@/components/settings/DevicesList.vue";
import { diagnosisOf, type MachineDiagnosis } from "@/lib/machine-diagnosis";
import { describeReach, parsePairingInput } from "@/lib/pairing-link";
import { normalizeManualCode } from "@/lib/pairing";
import { useSettingsNav } from "@/composables/use-settings-nav";
import { useHomeAccess } from "@/composables/use-home-access";
import { useMachineLines } from "@/composables/use-machine-lines";
import { useMachinePage } from "@/composables/use-machine-page";
import { ErrorNote } from "@/components/ui/error-note";
import { Spinner } from "@/components/ui/spinner";

/**
 * Settings → Machines: "Your machines" (every machine this client knows, each with how a phone reaches it), "Phones
 * and browsers" (every screen that has keys), and adding a machine. A machine's own page (token, addresses, who may
 * reach it, forgetting it) opens from its row. The list belongs to this client (it lives in this browser or desktop
 * app); each machine's name belongs to the machine, so a rename shows up on every client.
 */

const buttonPrimaryClass = "inline-flex items-center justify-center gap-2 rounded-btn bg-primary px-3 py-2 text-sm font-medium text-primary-foreground transition-opacity hover:opacity-90 disabled:cursor-not-allowed disabled:opacity-60";
const buttonSecondaryClass = "inline-flex items-center justify-center gap-2 rounded-btn border border-border bg-main-bg px-3 py-1.5 text-sm font-medium text-text transition-colors hover:border-accent/50 disabled:cursor-not-allowed disabled:opacity-60";
const inputClass = "w-full rounded-btn border border-border bg-main-bg px-3 py-2 text-sm text-text outline-none transition-colors placeholder:text-muted focus:border-accent";


const machines = useMachinesStore();
const { entries, home } = storeToRefs(machines);
const page = useMachinePage();
const lines = useMachineLines();
const { stateLine, capabilityLine, harnessVersions, location } = lines;
const homeAccess = useHomeAccess();
const { access, isDevice, loaded: accessLoaded, error: accessError, screens, notAnswering } = homeAccess;
const openEntry = computed(() => entries.value.find((entry) => entry.key === page.openKey.value) ?? null);

// ── Add ──────────────────────────────────────────────────────────────────────
const showAddForm = shallowRef(false);
const addForm = reactive({ link: "", token: "", code: "" });
type AddMode = "found" | "setup" | "server" | "link";
const addModes: AddMode[] = ["found", "setup", "server", "link"];
const modeLabels: Record<AddMode, string> = { found: "Found", setup: "Set it up from here", server: "On a new server", link: "Paste a pairing link" };
// Found is where Add a machine opens: a waiting machine needs nothing pasted.
const addMode = shallowRef<AddMode>("found");
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
  addMode.value = "found";
  await machines.loadCandidates();
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
const router = useRouter();

/** Makes that machine the one you're on and goes to its first page, in place. */
function workHere(key: string): void {
  machines.setCurrent(key);
  void router.navigate({ to: "/" });
}
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
let lookToken = 0;

watch(pairTarget, async (target) => {
  const mine = ++lookToken;
  found.value = null;
  previewError.value = null;
  previewDiagnosis.value = null;
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

const homeName = computed(() => home.value?.name ?? "This machine");
const deviceNames = computed(() => homeAccess.devices.value.map((device) => device.name));
const thisComputer = computed(() => access.value?.requiresToken
  ? "Signed in with the machine token"
  : "Signed in on loopback");

function onPublicUrlSaved(url: string): void {
  if (home.value) home.value = { ...home.value, publicUrl: url || null };
}

let stopPolling: (() => void) | null = null;
onMounted(() => {
  void machines.loadHome();
  // An answer in a conversation ("Always for …") may have changed a machine's setting since the list was read.
  void machines.syncFromServer();
  void homeAccess.load();
  stopPolling = machines.startPolling();
});
onUnmounted(() => {
  stopPolling?.();
  page.close();
});
</script>
<template>
  <MachinePage
    v-if="openEntry"
    :entry="openEntry"
  />
  <div
    v-else
    class="grid gap-6"
    data-testid="machines-section"
  >
    <SettingGroup
      id="machines"
      title="Your machines"
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
        <FoundMachinesCard
          v-if="addMode === 'found'"
          @added="onSetUp"
          @use-paste="pickMode('link')"
        />
        <MachineSetupCard
          v-else-if="addMode === 'setup'"
          @added="onSetUp"
          @use-link="onUseLink"
          @running="setupRunning = $event"
        />
        <NewServerCard
          v-else-if="addMode === 'server'"
          @joined="onSetUp"
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
              placeholder="http://100.64.90.72:6262/add-machine#ABCD-EFGH"
              :disabled="isAdding"
              data-testid="machines-add-link"
            >
            <span class="text-xs text-muted">Or just the machine's address, then its code.</span>
          </label>
          <ErrorNote
            v-if="pasted?.kind === 'invalid'"
            :message="pasted.message"
            compact
            data-testid="machines-add-link-invalid"
          />
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
            <Spinner size="sm" />
            Looking for the machine…
          </p>
          <ErrorNote
            v-if="previewError"
            :message="previewError"
            data-testid="machines-add-preview-error"
          >
            <template #details>
              <MachineDiagnosisPanel
                v-if="previewDiagnosis"
                :diagnosis="previewDiagnosis"
              />
            </template>
          </ErrorNote>
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
          </div>
        </template>
        <ErrorNote
          v-if="addError"
          :message="addError"
          data-testid="machines-add-error"
        >
          <template #details>
            <MachineDiagnosisPanel
              v-if="addDiagnosis"
              :diagnosis="addDiagnosis"
            />
          </template>
        </ErrorNote>
        <div class="flex flex-wrap gap-2">
          <button
            v-if="addMode === 'link'"
            type="submit"
            :class="buttonPrimaryClass"
            :disabled="isAdding || !canAdd"
            data-testid="machines-add-submit"
          >
            <Spinner v-if="isAdding" />
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
        aria-label="Your machines"
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
            <span class="machine-card__name">
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
              class="machine-card__state"
              :class="lines.reachOf(entry, access?.addresses ?? []).tone === 'out' ? 'machine-card__state--bad' : ''"
              data-testid="machine-reach-line"
            >{{ lines.reachOf(entry, access?.addresses ?? []).text }}</span>
            <span
              v-if="capabilityLine(entry)"
              class="machine-card__capabilities"
              :title="harnessVersions(entry)"
              data-testid="machine-capabilities"
            >{{ capabilityLine(entry) }}</span>
          </div>
          <div class="machine-card__actions">
            <button
              v-if="!entry.isLive"
              type="button"
              :class="buttonSecondaryClass"
              data-testid="machine-open"
              @click="workHere(entry.key)"
            >
              Work here
            </button>
            <button
              type="button"
              :class="buttonSecondaryClass"
              :aria-label="`${entry.name}'s page`"
              data-testid="machine-manage"
              @click="page.open(entry.key)"
            >
              Manage
            </button>
          </div>
        </li>
      </ul>
    </SettingGroup>

    <SettingGroup
      id="phones"
      title="Phones and browsers"
      hint="Every phone and browser that has keys, wherever it connected. Remove one and it loses them on every machine."
      data-testid="machine-phones-group"
    >
      <ErrorNote
        v-if="accessError"
        :message="accessError"
        class="mx-4 my-3"
      />
      <p
        v-else-if="!access"
        class="px-4 py-3 text-sm text-muted"
      >
        <template v-if="isDevice">
          This browser is signed in as a paired device. Adding phones happens on the machine itself.
        </template>
        <template v-else>
          {{ accessLoaded ? "This Fleet doesn't hand out access tokens: it signs people in with an identity provider, or it's too old for machines." : "Loading…" }}
        </template>
      </p>
      <div
        v-else
        class="px-4 py-3"
        data-testid="machine-phones"
      >
        <AddPhonePanel
          :machine-name="homeName"
          :public-url="home?.publicUrl ?? null"
          :addresses="access.addresses"
          :device-names="deviceNames"
          @refresh="homeAccess.refreshDevices()"
          @saved-url="onPublicUrlSaved"
          @reach-changed="homeAccess.load()"
        />
        <DevicesList
          :devices="screens"
          :this-computer="thisComputer"
          :not-answering="notAnswering.map((machine) => machine.name)"
          hide-this-machine
          @removed="homeAccess.refreshDevices()"
        />
      </div>
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
  color: var(--coral-text);
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

.machine-card__state--ok {
  color: var(--running-text);
}

.machine-card__state--bad {
  color: var(--error-text);
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
</style>
