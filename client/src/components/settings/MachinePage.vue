<script setup lang="ts">
import { computed, onMounted, reactive, shallowRef, watch } from "vue";
import { storeToRefs } from "pinia";
import { useRouter } from "@tanstack/vue-router";
import { Check, Copy, Eye, EyeOff, RefreshCw } from "lucide-vue-next";
import {
  agentWorkLevelName,
  useMachinesStore,
  type MachineAgentWork,
  type MachineEntry,
} from "@/stores/machines";
import SettingGroup from "@/components/settings/SettingGroup.vue";
import ReachDialog from "@/components/settings/ReachDialog.vue";
import TailscaleCheckNote from "@/components/settings/TailscaleCheckNote.vue";
import MachineDiagnosisPanel from "@/components/settings/MachineDiagnosisPanel.vue";
import MachineJobSeats from "@/components/settings/MachineJobSeats.vue";
import MachineAddressList from "@/components/settings/MachineAddressList.vue";
import { useJobsFeature } from "@/composables/use-jobs-feature";
import { usePreferencesStore } from "@/stores/preferences";
import { useHomeAccess } from "@/composables/use-home-access";
import { useMachineLines } from "@/composables/use-machine-lines";
import { useMachinePage } from "@/composables/use-machine-page";
import { Button } from "@/components/ui/button";
import { ErrorNote } from "@/components/ui/error-note";
import { confirmAction } from "@/components/ui/confirm";

/**
 * One machine's own page in Settings → Machines: the parts most people never need. Its name and address, who it lets
 * agents send work, the token and addresses other devices use, who can reach it. The list has the everyday ones.
 */
const props = defineProps<{ entry: MachineEntry }>();

const inputClass = "w-full rounded-btn border border-border bg-main-bg px-3 py-2 text-sm text-text outline-none transition-colors placeholder:text-muted focus:border-accent";

const machines = useMachinesStore();
const { entries } = storeToRefs(machines);
const { isJobsEnabled: jobsOn } = useJobsFeature();
usePreferencesStore().ensureLoaded();
const page = useMachinePage();
const router = useRouter();
const lines = useMachineLines();
const { diagnosisFor, stateLine, capabilityLine, harnessVersions, addressesOf, routeLine, location } = lines;
const homeAccess = useHomeAccess();
const { access, isDevice, loaded: accessLoaded, error: accessError, reach } = homeAccess;

const rowError = reactive<Record<string, string | null>>({});
const busyKey = shallowRef<string | null>(null);
const renaming = shallowRef(false);
const renameValue = shallowRef("");
const tokenOpen = shallowRef(false);
const tokenValue = shallowRef("");

/** Home is the machine this client is signed in to; its page also holds how others reach it. */
const isHome = computed(() => props.entry.isHome);
const reachText = computed(() => lines.reachOf(props.entry, access.value?.addresses ?? []));

function workHere(): void {
  machines.setCurrent(props.entry.key);
  void router.navigate({ to: "/" });
}

function startRename(): void {
  renaming.value = true;
  renameValue.value = props.entry.name;
  rowError[props.entry.key] = null;
}

async function saveRename(): Promise<void> {
  busyKey.value = props.entry.key;
  try {
    await machines.renameMachine(props.entry.key, renameValue.value.trim());
    renaming.value = false;
  } catch (error) {
    rowError[props.entry.key] = error instanceof Error ? error.message : String(error);
  } finally {
    busyKey.value = null;
  }
}

async function saveToken(): Promise<void> {
  busyKey.value = props.entry.key;
  try {
    await machines.updateToken(props.entry.key, tokenValue.value);
    tokenOpen.value = false;
    tokenValue.value = "";
  } catch (error) {
    rowError[props.entry.key] = error instanceof Error ? error.message : String(error);
  } finally {
    busyKey.value = null;
  }
}

// ── Agents sending work ──────────────────────────────────────────────────────
// "Agents here can send it work", on each other machine in home's own list (only its owner sees that list). This
// machine's own card has none: it's where agents send work from.
const AGENT_WORK_CHOICES: { value: MachineAgentWork; label: string }[] = [
  { value: "ask", label: "Ask" },
  { value: "always", label: "Always" },
  { value: "never", label: "Never" },
];

function showsAgentWork(entry: MachineEntry): boolean {
  return machines.agentWorkEntries.some((machine) => machine.key === entry.key);
}


async function setAgentWork(entry: MachineEntry, value: MachineAgentWork): Promise<void> {
  if (machines.agentWork[entry.key] === value) return;
  busyKey.value = entry.key;
  rowError[entry.key] = null;
  try {
    await machines.setAgentWork(entry.key, value);
  } catch (error) {
    rowError[entry.key] = error instanceof Error ? error.message : String(error);
  } finally {
    busyKey.value = null;
  }
}

async function forget(): Promise<void> {
  const entry = props.entry;
  const confirmed = await confirmAction(!entry.sshDestination
    ? {
        title: `Forget ${entry.name} on every machine in your Fleet?`,
        body: `Every machine drops it from its list and its keys stop working, even on machines that are off now: they hear when they're back. Its sessions stay on ${entry.name}. To use it again, add it again with its pairing code.`,
        confirmLabel: `Forget ${entry.name}`,
        destructive: true,
      }
    : {
        title: `Forget ${entry.name}?`,
        body: `Its sessions stay on ${entry.name}; this device just stops listing them.`,
        confirmLabel: "Forget machine",
        destructive: true,
      });
  if (!confirmed) return;
  machines.forgetMachine(entry.key);
  page.close();
}

function saveAddresses(entry: MachineEntry, urls: string[]): Promise<void> {
  rowError[entry.key] = null;
  return machines.reorderAddresses(entry.key, urls);
}

// ── This machine's access ────────────────────────────────────────────────────
const showToken = shallowRef(false);
const copied = shallowRef<string | null>(null);
const isReplacing = shallowRef(false);
const homeName = computed(() => props.entry.name);
const reachOpen = shallowRef(false);
const reachLabel = computed(() => {
  const state = reach.state.value;
  if (!state?.available) return null;
  return { off: null, "tailscale-and-home": "Tailscale and home network", home: "Home network only", own: state.ownAddress ?? "Your own address" }[state.mode];
});

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
  const confirmed = await confirmAction({
    title: "Replace this machine's token?",
    body: "Every device using the old one loses access until you give it the new one.",
    confirmLabel: "Replace token",
    destructive: true,
  });
  if (!confirmed) return;
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

watch(entries, () => {
  // The machine was forgotten: its page has nothing left to show.
  if (!entries.value.some((entry) => entry.key === props.entry.key)) page.close();
});
onMounted(() => {
  if (props.entry.isHome) void homeAccess.load();
});
</script>

<template>
  <div
    class="grid gap-6"
    data-testid="machine-page"
    :data-machine="entry.key"
  >
    <SettingGroup
      id="machine-overview"
      title="About this machine"
    >
      <div class="machine-card">
        <span
          class="machine-card__dot"
          :class="`machine-card__dot--${stateLine(entry).tone}`"
          aria-hidden="true"
        />
        <div class="machine-card__names">
          <form
            v-if="renaming"
            class="flex flex-wrap items-center gap-2"
            @submit.prevent="saveRename()"
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
            <Button
              type="submit"
              variant="outline"
              size="sm"
              :disabled="busyKey === entry.key"
            >
              Save
            </Button>
            <Button
              type="button"
              variant="outline"
              size="sm"
              @click="renaming = false"
            >
              Cancel
            </Button>
          </form>
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
            class="machine-card__state"
            :class="reachText.tone === 'out' ? 'machine-card__state--bad' : ''"
            data-testid="machine-reach-line"
          >{{ reachText.text }}</span>
          <template
            v-for="route in routeLine(entry) ? [routeLine(entry)!] : []"
            :key="route.text"
          >
            <span
              class="machine-card__state machine-card__state--ok"
              data-testid="machine-route"
            >{{ route.text }}<span
              v-if="route.note"
              class="machine-card__route-note"
              data-testid="machine-route-note"
            > · {{ route.note }}</span></span>
          </template>
          <span
            v-if="capabilityLine(entry)"
            class="machine-card__capabilities"
            :title="harnessVersions(entry)"
            data-testid="machine-capabilities"
          >{{ capabilityLine(entry) }}</span>
          <div
            v-if="showsAgentWork(entry)"
            class="machine-card__agents"
            data-testid="machine-agent-work"
          >
            <span class="machine-card__agents-row">
              <span :id="`agent-work-${entry.key}`">Agents here can send it work</span>
              <span
                class="machine-card__segments"
                role="radiogroup"
                :aria-labelledby="`agent-work-${entry.key}`"
              >
                <button
                  v-for="choice in AGENT_WORK_CHOICES"
                  :key="choice.value"
                  type="button"
                  role="radio"
                  class="machine-card__segment"
                  :class="{ 'machine-card__segment--on': machines.agentWork[entry.key] === choice.value }"
                  :aria-checked="machines.agentWork[entry.key] === choice.value"
                  :disabled="busyKey === entry.key"
                  :data-testid="`machine-agent-work-${choice.value}`"
                  @click="setAgentWork(entry, choice.value)"
                >{{ choice.label }}</button>
              </span>
            </span>
            <span
              class="machine-card__agents-level"
              data-testid="machine-agent-work-level"
            >Work there runs under {{ entry.name }}'s level: {{ agentWorkLevelName(entry) }}.</span>
          </div>
          <MachineJobSeats
            v-for="key in jobsOn ? [entry.key] : []"
            :key="key"
            :entry-key="key"
          />
          <details
            v-if="diagnosisFor(entry)"
            class="mt-1 text-xs"
            data-testid="machine-details"
          >
            <summary class="cursor-pointer text-muted">
              Details
            </summary>
            <MachineDiagnosisPanel
              :diagnosis="diagnosisFor(entry)!"
              retryable
              @retry="machines.retryMachine(entry.key)"
            />
          </details>
          <ErrorNote
            v-if="rowError[entry.key]"
            :message="rowError[entry.key]"
            compact
            class="mt-1"
          />
        </div>
        <div
          v-if="!renaming"
          class="machine-card__actions"
        >
          <Button
            v-if="!entry.isLive"
            type="button"
            variant="outline"
            size="sm"
            data-testid="machine-open"
            @click="workHere()"
          >
            Work here
          </Button>
          <Button
            type="button"
            variant="outline"
            size="sm"
            data-testid="machine-rename"
            @click="startRename()"
          >
            Rename
          </Button>
          <Button
            v-if="!entry.isHome"
            type="button"
            variant="outline"
            size="sm"
            class="hover:border-error/60 hover:text-error-text"
            data-testid="machine-forget"
            @click="forget()"
          >
            Forget
          </Button>
        </div>
      </div>
    </SettingGroup>

    <SettingGroup
      v-if="!isHome"
      id="machine-keys"
      title="Key and addresses"
      hint="What this browser uses to reach the machine."
    >
      <div
        v-for="list in addressesOf(entry).length ? [addressesOf(entry)] : []"
        :key="list.length"
        class="px-4 py-3"
      >
        <MachineAddressList
          :name="entry.name"
          :addresses="list"
          :in-use="entry.baseUrl"
          :save="(urls) => saveAddresses(entry, urls)"
          @failed="rowError[entry.key] = $event"
        />
      </div>
      <form
        v-if="tokenOpen"
        class="flex flex-wrap items-center gap-2 px-4 py-3"
        @submit.prevent="saveToken()"
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
        <Button
          type="submit"
          variant="outline"
          size="sm"
          :disabled="busyKey === entry.key || !tokenValue.trim()"
        >
          Save
        </Button>
        <Button
          type="button"
          variant="outline"
          size="sm"
          @click="tokenOpen = false"
        >
          Cancel
        </Button>
      </form>
      <div
        v-else
        class="flex flex-wrap items-center gap-3 px-4 py-3"
      >
        <p class="max-w-prose text-sm text-muted">
          This browser holds a key for {{ entry.name }}. If it stopped working, give it a new one.
        </p>
        <Button
          type="button"
          variant="outline"
          size="sm"
          data-testid="machine-new-token"
          @click="tokenOpen = true"
        >
          New token
        </Button>
      </div>
    </SettingGroup>

    <SettingGroup
      v-if="isHome"
      id="phones"
      title="Who can reach it"
      hint="The access token, the addresses and who may reach this machine."
      data-testid="machine-access"
    >
      <ReachDialog
        v-if="reach.state.value?.available"
        v-model:open="reachOpen"
        :machine-name="homeName"
        @changed="homeAccess.load()"
      />
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
          This browser is signed in as a paired device. Seeing the token happens on the machine itself.
        </template>
        <template v-else>
          {{ accessLoaded ? "This Fleet doesn't hand out access tokens: it signs people in with an identity provider, or it's too old for machines." : "Loading…" }}
        </template>
      </p>

      <template v-else>
        <div
          v-if="reach.state.value?.available && reach.state.value.mode === 'off'"
          class="flex flex-wrap items-center gap-3 px-4 py-3"
          data-testid="machine-access-loopback"
        >
          <p class="max-w-prose text-sm text-muted">
            Reachable from <b class="text-text">this machine only</b>. Turn it on to use {{ homeName }} from your phone
            or another machine.
          </p>
          <Button
            variant="outline"
            size="sm"
            data-testid="machine-reach-open"
            @click="reachOpen = true"
          >
            Let others reach {{ homeName }}
          </Button>
        </div>
        <div
          v-else-if="reachLabel"
          class="flex flex-wrap items-center gap-3 px-4 py-3"
          data-testid="machine-access-reach"
        >
          <p class="max-w-prose text-sm text-muted">
            Reachable from other devices: <b class="text-text">{{ reachLabel }}</b>. Each one needs its own key.
          </p>
          <Button
            variant="outline"
            size="sm"
            data-testid="machine-reach-open"
            @click="reachOpen = true"
          >
            Change
          </Button>
        </div>
        <div
          v-if="reach.state.value?.mode === 'tailscale-and-home' && reach.state.value.tailscale && !reach.state.value.tailscale.covered"
          class="px-4 py-3"
        >
          <TailscaleCheckNote :check="reach.state.value.tailscale" />
        </div>
        <p
          v-else-if="!access.remoteReachable && !access.requiresToken"
          class="max-w-prose px-4 py-3 text-sm text-muted"
          data-testid="machine-access-loopback"
        >
          Only this machine can reach it: Fleet listens on {{ access.host }}:{{ access.port }}. To add it from
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
            <Button
              v-if="access.tokenSource !== 'environment'"
              type="button"
              variant="outline"
              size="sm"
              :disabled="isReplacing"
              @click="replaceToken"
            >
              <RefreshCw
                :size="14"
                aria-hidden="true"
              />
              Replace token
            </Button>
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
  border-radius: var(--radius-btn);
  font-family: var(--font-mono);
  font-size: 10px;
  font-weight: 600;
  letter-spacing: 0.05em;
  text-transform: uppercase;
  color: var(--muted);
}

.machine-card__live {
  padding: 1px 5px;
  border-radius: var(--radius-btn);
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

.machine-card__route-note {
  color: var(--muted);
}

.machine-card__capabilities {
  font-family: var(--font-mono);
  font-size: 11.5px;
  color: var(--muted);
}

.machine-card__agents {
  display: grid;
  gap: 4px;
  margin-top: 6px;
  font-size: 12.5px;
  color: var(--text);
}

.machine-card__agents-row {
  display: inline-flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 10px;
}

.machine-card__segments {
  display: inline-flex;
  overflow: hidden;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
}

.machine-card__segment {
  padding: 2px 10px;
  color: var(--muted);
  font-size: 12.5px;
  transition: background-color var(--transition), color var(--transition);
}

.machine-card__segment + .machine-card__segment {
  border-left: 1px solid var(--border);
}

.machine-card__segment:hover:not(:disabled) {
  color: var(--text);
}

.machine-card__segment--on {
  background: color-mix(in srgb, var(--accent) 18%, transparent);
  color: var(--text);
  font-weight: 600;
}

.machine-card__segment:disabled {
  cursor: progress;
}

.machine-card__agents-level {
  color: var(--muted);
  font-size: 12px;
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
  border-radius: var(--radius-btn);
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
