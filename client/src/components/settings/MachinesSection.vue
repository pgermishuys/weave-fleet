<script setup lang="ts">
import { computed, onMounted, onUnmounted, reactive, shallowRef } from "vue";
import { storeToRefs } from "pinia";
import { AlertCircle, Check, Copy, Eye, EyeOff, LoaderCircle, Plus, RefreshCw } from "lucide-vue-next";
import { useMachinesStore, type MachineAccess, type MachineEntry } from "@/stores/machines";

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
const { entries, others, home } = storeToRefs(machines);

// ── Add ──────────────────────────────────────────────────────────────────────
const showAddForm = shallowRef(false);
const addForm = reactive({ url: "", token: "" });
const isAdding = shallowRef(false);
const addError = shallowRef<string | null>(null);
const justAdded = shallowRef<string | null>(null);

async function addMachine(): Promise<void> {
  isAdding.value = true;
  addError.value = null;
  try {
    const machine = await machines.addMachine(addForm.url, addForm.token);
    justAdded.value = machine.name;
    addForm.url = "";
    addForm.token = "";
    showAddForm.value = false;
  } catch (error) {
    addError.value = error instanceof Error ? error.message : String(error);
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

function forget(entry: MachineEntry): void {
  if (!window.confirm(`Forget ${entry.name}? Its sessions stay on ${entry.name}; this device just stops listing them.`)) return;
  machines.forgetMachine(entry.key);
}

function stateLine(entry: MachineEntry): { text: string; tone: "ok" | "bad" | "muted" } {
  if (entry.isLive) return { text: "live", tone: "ok" };
  const state = others.value[entry.key];
  if (state?.error) return { text: state.error, tone: "bad" };
  if (state?.loadedAt) return { text: `reachable · ${state.sessions.length} sessions`, tone: "ok" };
  return { text: "checking…", tone: "muted" };
}

function location(entry: MachineEntry): string {
  return entry.isHome ? "this machine" : entry.baseUrl;
}

// ── This machine's access ────────────────────────────────────────────────────
const access = shallowRef<MachineAccess | null>(null);
const accessLoaded = shallowRef(false);
const accessError = shallowRef<string | null>(null);
const showToken = shallowRef(false);
const copied = shallowRef<string | null>(null);
const isReplacing = shallowRef(false);

const homeName = computed(() => home.value?.name ?? "This machine");

async function loadAccess(): Promise<void> {
  accessError.value = null;
  try {
    access.value = await machines.loadHomeAccess();
  } catch (error) {
    accessError.value = error instanceof Error ? error.message : String(error);
  } finally {
    accessLoaded.value = true;
  }
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
  <section
    class="rounded-card border border-border bg-card-bg p-6 shadow-sm"
    data-testid="machines-section"
  >
    <div class="flex flex-col gap-3 md:flex-row md:items-start md:justify-between">
      <div class="space-y-1">
        <h2 class="text-lg font-semibold text-text">
          Machines
        </h2>
        <p class="max-w-prose text-sm text-muted">
          Every machine runs its own Fleet and keeps its own sessions, repositories and sign-ins. A session stays
          on the machine it started on. The sessions list shows them all; the one you're working in is live.
        </p>
      </div>

      <button
        v-if="!showAddForm"
        type="button"
        :class="buttonPrimaryClass"
        data-testid="machines-add"
        @click="showAddForm = true; addError = null; justAdded = null"
      >
        <Plus
          :size="16"
          aria-hidden="true"
        />
        Add a machine
      </button>
    </div>

    <form
      v-if="showAddForm"
      class="mt-5 grid gap-3 rounded-card border border-border bg-main-bg p-4"
      data-testid="machines-add-form"
      @submit.prevent="addMachine"
    >
      <div>
        <h3 class="text-sm font-semibold text-text">
          Add a machine
        </h3>
        <p class="mt-1 text-xs text-muted">
          On the other machine, open Settings → Machines and copy its address and token. It has to listen beyond
          127.0.0.1: start it with <code class="machine-code">--host 0.0.0.0</code>, or behind
          <code class="machine-code">tailscale serve</code> with <code class="machine-code">--require-token</code>.
        </p>
      </div>
      <label class="grid gap-1 text-sm text-text">
        <span class="text-xs font-medium uppercase tracking-wide text-muted">Address</span>
        <input
          v-model="addForm.url"
          type="text"
          inputmode="url"
          autocomplete="off"
          spellcheck="false"
          :class="inputClass"
          placeholder="http://100.64.90.72:2113"
          :disabled="isAdding"
          data-testid="machines-add-url"
        >
      </label>
      <label class="grid gap-1 text-sm text-text">
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
      </label>
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
        <span>{{ addError }}</span>
      </div>
      <div class="flex flex-wrap gap-2">
        <button
          type="submit"
          :class="buttonPrimaryClass"
          :disabled="isAdding || !addForm.url.trim() || !addForm.token.trim()"
          data-testid="machines-add-submit"
        >
          <LoaderCircle
            v-if="isAdding"
            :size="16"
            class="animate-spin"
            aria-hidden="true"
          />
          {{ isAdding ? "Checking…" : "Add machine" }}
        </button>
        <button
          type="button"
          :class="buttonSecondaryClass"
          :disabled="isAdding"
          @click="showAddForm = false"
        >
          Cancel
        </button>
      </div>
    </form>

    <p
      v-if="justAdded"
      class="mt-4 flex items-center gap-2 text-sm text-text"
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
      class="mt-5 grid gap-2"
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
          <span class="machine-card__url">{{ location(entry) }}</span>
          <span
            class="machine-card__state"
            :class="`machine-card__state--${stateLine(entry).tone}`"
          >{{ stateLine(entry).text }}</span>

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

    <div
      class="mt-8 border-t border-border pt-6"
      data-testid="machine-access"
    >
      <h3 class="text-sm font-semibold text-text">
        Let other devices reach {{ homeName }}
      </h3>

      <p
        v-if="accessError"
        class="mt-2 text-sm text-red-400"
        role="alert"
      >
        {{ accessError }}
      </p>

      <p
        v-else-if="!access"
        class="mt-2 text-sm text-muted"
      >
        {{ accessLoaded ? "This Fleet doesn't hand out access tokens: it signs people in with an identity provider, or it's too old for machines." : "Loading…" }}
      </p>

      <template v-else>
        <p
          v-if="!access.remoteReachable && !access.requiresToken"
          class="mt-2 max-w-prose text-sm text-muted"
          data-testid="machine-access-loopback"
        >
          Only this computer can reach it: Fleet listens on {{ access.host }}:{{ access.port }}. To add it from
          another device, start Fleet with <code class="machine-code">--host 0.0.0.0</code> (or your tailnet
          address), or put <code class="machine-code">tailscale serve</code> in front of it and start Fleet with
          <code class="machine-code">--require-token</code>.
        </p>
        <p
          v-else-if="!access.remoteReachable"
          class="mt-2 max-w-prose text-sm text-muted"
        >
          Fleet listens on {{ access.host }}:{{ access.port }} and asks every request for the token, so a proxy
          such as <code class="machine-code">tailscale serve</code> can forward other devices to it. Use the
          proxy's address.
        </p>

        <div
          v-if="access.addresses.length"
          class="mt-3 grid gap-1"
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

        <div class="mt-4 grid gap-1">
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
              Whoever has it can do anything Fleet can on this machine. Replacing it locks out every device that
              has the old one.
            </template>
          </p>
        </div>
      </template>
    </div>
  </section>
</template>

<style scoped>
.machine-card {
  display: flex;
  flex-wrap: wrap;
  align-items: flex-start;
  gap: 12px;
  padding: 12px 14px;
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  background: var(--main-bg);
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
