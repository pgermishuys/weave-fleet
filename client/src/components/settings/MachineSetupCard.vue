<script setup lang="ts">
import { computed, onMounted, reactive, shallowRef, watch } from "vue";
import { storeToRefs } from "pinia";
import { AlertCircle, Check, Circle, LoaderCircle, Minus, X } from "lucide-vue-next";
import MachineAddNote from "@/components/settings/MachineAddNote.vue";
import MachineDiagnosisPanel from "@/components/settings/MachineDiagnosisPanel.vue";
import { diagnosisOf, type MachineDiagnosis } from "@/lib/machine-diagnosis";
import { useMachinesStore, type CandidateProbe, type MachineCandidate, type SetupStep } from "@/stores/machines";

/**
 * Add a machine → Set it up from here: the machines Fleet can already see (ssh config, Tailscale), what is on each, and
 * the steps of setting one up over SSH. A machine Fleet can't log in to, or can't set up, offers the pairing link instead.
 */
const emit = defineEmits<{
  added: [name: string, machineId: string];
  useLink: [name: string];
  /** Whether a set-up is running, so the dialog can say Close instead of Cancel. */
  running: [running: boolean];
}>();

const MAX_ROWS = 8;
const PROBES_AT_ONCE = 3;

const buttonPrimaryClass = "inline-flex items-center justify-center gap-2 rounded-btn bg-primary px-3 py-1.5 text-sm font-medium text-white transition-opacity hover:opacity-90 disabled:cursor-not-allowed disabled:opacity-60";
const buttonSecondaryClass = "inline-flex items-center justify-center gap-2 rounded-btn border border-border bg-main-bg px-3 py-1.5 text-sm font-medium text-text transition-colors hover:border-accent/50 disabled:cursor-not-allowed disabled:opacity-60";
const inputClass = "w-full rounded-btn border border-border bg-main-bg px-3 py-2 text-sm text-text outline-none transition-colors placeholder:text-muted focus:border-accent";

const machines = useMachinesStore();
const { candidates, setupCommands } = storeToRefs(machines);

const search = shallowRef("");
const probes = reactive<Record<string, CandidateProbe | "looking">>({});

/** The listed machines that match the search, or the typed `user@host` when none do. */
const rows = computed<MachineCandidate[]>(() => {
  const text = search.value.trim().toLowerCase();
  const all = candidates.value ?? [];
  const matching = all.filter((candidate) => `${candidate.name} ${candidate.destination}`.toLowerCase().includes(text));
  const typed = search.value.trim();
  const isTyped = matching.length === 0 && /^[A-Za-z0-9_.@:%[\]-]+$/.test(typed);
  return (isTyped ? [{ name: typed, destination: typed, source: "typed", os: null }] : matching).slice(0, MAX_ROWS);
});

let running = 0;
const waiting: MachineCandidate[] = [];

function look(candidate: MachineCandidate): void {
  if (probes[candidate.destination]) return;
  if (candidate.os === "windows") {
    probes[candidate.destination] = { state: "unsupported", os: "windows", version: null, error: null, diagnosis: null };
    return;
  }

  probes[candidate.destination] = "looking";
  waiting.push(candidate);
  next();
}

function next(): void {
  while (running < PROBES_AT_ONCE && waiting.length > 0) {
    const candidate = waiting.shift()!;
    running++;
    machines.probeCandidate(candidate.destination)
      .then((probe) => { probes[candidate.destination] = probe; })
      .catch((error: unknown) => {
        probes[candidate.destination] = {
          state: "cantLogIn", os: null, version: null, diagnosis: diagnosisOf(error),
          error: error instanceof Error ? error.message : String(error),
        };
      })
      .finally(() => { running--; next(); });
  }
}

watch(rows, (shown) => shown.forEach(look), { immediate: true });
onMounted(() => { if (candidates.value === null) void machines.loadCandidates(); });

function osLabel(os: string | null): string | null {
  return os ? ({ linux: "Linux", macos: "macOS", windows: "Windows" } as Record<string, string>)[os] ?? null : null;
}

function probeFor(candidate: MachineCandidate): CandidateProbe | null {
  const probe = probes[candidate.destination];
  return probe && probe !== "looking" ? probe : null;
}

function statusLine(candidate: MachineCandidate): string {
  const probe = probeFor(candidate);
  if (!probe) return "Looking…";
  const os = osLabel(probe.os ?? candidate.os);
  const what = probe.state === "ready" ? `Fleet ${probe.version}`
    : probe.state === "noFleet" ? "SSH works · no Fleet yet"
      : probe.state === "unsupported" ? "Fleet sets machines up over SSH on Linux and macOS only"
        : "can't log in over SSH";
  return [os, what].filter(Boolean).join(" · ");
}

// ── Setting one up ───────────────────────────────────────────────────────────
const settingUp = shallowRef<MachineCandidate | null>(null);
const steps = shallowRef<SetupStep[]>([]);
const failure = shallowRef<{ message: string; diagnosis: MachineDiagnosis | null } | null>(null);
const showCommands = shallowRef(false);

async function setUp(candidate: MachineCandidate): Promise<void> {
  settingUp.value = candidate;
  steps.value = [];
  failure.value = null;
  try {
    const machine = await machines.setUpMachine(candidate.destination, (current) => { steps.value = current; });
    emit("added", machine.name, machine.id);
  } catch (error) {
    failure.value = { message: error instanceof Error ? error.message : String(error), diagnosis: diagnosisOf(error) };
  }
}

const isRunning = computed(() => settingUp.value !== null && failure.value === null);
watch(isRunning, (running) => emit("running", running));

function back(): void {
  settingUp.value = null;
  failure.value = null;
}

function actionLabel(candidate: MachineCandidate): string | null {
  const probe = probeFor(candidate);
  if (!probe) return null;
  return probe.state === "ready" ? "Add" : probe.state === "noFleet" ? "Set up" : "Use a link";
}

function act(candidate: MachineCandidate): void {
  const probe = probeFor(candidate);
  if (probe?.state === "ready" || probe?.state === "noFleet") void setUp(candidate);
  else emit("useLink", candidate.name);
}
</script>

<template>
  <div
    class="grid gap-3"
    data-testid="machines-setup-card"
  >
    <template v-if="!settingUp">
      <p class="text-xs text-muted">
        Over SSH, with your own keys. Fleet installs itself on the other machine if it isn't there. No password is asked.
      </p>
      <input
        v-model="search"
        type="text"
        autocomplete="off"
        autocapitalize="none"
        spellcheck="false"
        :class="inputClass"
        placeholder="Search machines, or type user@host"
        data-testid="machines-setup-search"
      >
      <p
        v-if="candidates !== null && rows.length === 0"
        class="text-xs text-muted"
        data-testid="machines-setup-empty"
      >
        Fleet doesn't see any machines in ~/.ssh/config or on Tailscale. Type user@host, or use a pairing link.
      </p>
      <ul
        class="m-0 grid list-none gap-2 p-0"
        aria-label="Machines Fleet can see"
      >
        <li
          v-for="candidate in rows"
          :key="candidate.destination"
          class="flex items-center gap-3 rounded-card border border-border bg-card-bg px-3 py-2"
          data-testid="machines-setup-row"
        >
          <div class="min-w-0 flex-1">
            <p class="m-0 flex items-center gap-2 text-sm font-medium text-text">
              {{ candidate.name }}
              <span
                v-if="candidate.source !== 'typed'"
                class="rounded-btn border border-border px-1.5 text-[11px] font-normal text-muted"
              >{{ candidate.source }}</span>
            </p>
            <p
              class="m-0 mt-0.5 flex items-center gap-1.5 text-xs text-muted"
              data-testid="machines-setup-status"
            >
              <LoaderCircle
                v-if="!probeFor(candidate)"
                :size="12"
                class="animate-spin"
                aria-hidden="true"
              />
              {{ statusLine(candidate) }}
            </p>
          </div>
          <button
            v-if="actionLabel(candidate)"
            type="button"
            :class="actionLabel(candidate) === 'Use a link' ? buttonSecondaryClass : buttonPrimaryClass"
            data-testid="machines-setup-action"
            @click="act(candidate)"
          >
            {{ actionLabel(candidate) }}
          </button>
        </li>
      </ul>
    </template>

    <template v-else>
      <div class="grid gap-3 rounded-card border border-border bg-card-bg p-3">
        <p
          class="m-0 text-sm font-medium text-text"
          data-testid="machines-setup-title"
        >
          Setting up {{ settingUp.name }}
        </p>
        <ol
          class="m-0 grid list-none gap-2 p-0"
          data-testid="machines-setup-steps"
        >
          <li
            v-for="step in steps"
            :key="step.key"
            class="flex items-start gap-2 text-sm"
            :data-step="step.key"
            :data-state="step.state"
          >
            <LoaderCircle
              v-if="step.state === 'running'"
              :size="16"
              class="mt-0.5 shrink-0 animate-spin text-accent"
              aria-hidden="true"
            />
            <Check
              v-else-if="step.state === 'done'"
              :size="16"
              class="mt-0.5 shrink-0 text-[var(--running)]"
              aria-hidden="true"
            />
            <Minus
              v-else-if="step.state === 'skipped'"
              :size="16"
              class="mt-0.5 shrink-0 text-muted"
              aria-hidden="true"
            />
            <X
              v-else-if="step.state === 'failed'"
              :size="16"
              class="mt-0.5 shrink-0 text-red-300"
              aria-hidden="true"
            />
            <Circle
              v-else
              :size="16"
              class="mt-0.5 shrink-0 text-muted"
              aria-hidden="true"
            />
            <div>
              <span :class="step.state === 'pending' ? 'text-muted' : 'text-text'">{{ step.label }}</span>
              <span
                v-if="step.detail"
                class="block text-xs text-muted"
              >{{ step.detail }}</span>
            </div>
          </li>
        </ol>
        <MachineAddNote :name="settingUp.name" />
        <button
          type="button"
          class="justify-self-start text-xs text-muted underline-offset-2 hover:text-text hover:underline"
          data-testid="machines-setup-commands-toggle"
          @click="showCommands = !showCommands"
        >
          {{ showCommands ? "Hide the commands" : "Show the commands" }}
        </button>
        <pre
          v-if="showCommands"
          class="m-0 overflow-x-auto rounded-btn border border-border bg-main-bg px-3 py-2 font-mono text-xs text-text"
          data-testid="machines-setup-commands"
        ><template
          v-for="command in setupCommands"
          :key="command"
        >$ {{ command }}
</template></pre>
      </div>
      <div
        v-if="failure"
        class="flex items-start gap-2 rounded-card border border-red-500/30 bg-red-500/10 px-3 py-2 text-sm text-red-300"
        role="alert"
        data-testid="machines-setup-error"
      >
        <AlertCircle
          :size="16"
          class="mt-0.5 shrink-0"
          aria-hidden="true"
        />
        <div>
          <span>{{ failure.diagnosis?.headline ?? failure.message }}</span>
          <MachineDiagnosisPanel
            v-if="failure.diagnosis"
            :diagnosis="failure.diagnosis"
          />
        </div>
      </div>
      <div
        v-if="failure"
        class="flex flex-wrap gap-2"
      >
        <button
          type="button"
          :class="buttonPrimaryClass"
          data-testid="machines-setup-retry"
          @click="setUp(settingUp)"
        >
          Try again
        </button>
        <button
          type="button"
          :class="buttonSecondaryClass"
          data-testid="machines-setup-use-link"
          @click="emit('useLink', settingUp.name)"
        >
          Use a link
        </button>
        <button
          type="button"
          :class="buttonSecondaryClass"
          data-testid="machines-setup-back"
          @click="back"
        >
          Back
        </button>
      </div>
      <p
        v-else-if="isRunning"
        class="text-xs text-muted"
      >
        Fleet keeps going if you close this. This can take a minute or two.
      </p>
    </template>
  </div>
</template>
