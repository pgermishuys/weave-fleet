<script setup lang="ts">
import { onBeforeUnmount, onMounted, reactive, shallowRef } from "vue";
import { storeToRefs } from "pinia";
import { Radar } from "lucide-vue-next";
import MachineAddNote from "@/components/settings/MachineAddNote.vue";
import MachineDiagnosisPanel from "@/components/settings/MachineDiagnosisPanel.vue";
import { Button } from "@/components/ui/button";
import { EmptyState } from "@/components/ui/empty-state";
import { ErrorNote } from "@/components/ui/error-note";
import { Spinner } from "@/components/ui/spinner";
import { diagnosisOf, type MachineDiagnosis } from "@/lib/machine-diagnosis";
import { normalizeManualCode } from "@/lib/pairing";
import { useMachinesStore, type FoundMachine } from "@/stores/machines";

/**
 * Add a machine → Found: machines that are waiting to be added (someone ran `fleet node pair` or `fleet node up` on them),
 * seen on Tailscale and the home network. Fleet shows the first half of the code the terminal shows; adding one needs the
 * rest, typed from that terminal, so the machine you add is the one in front of you.
 */
const emit = defineEmits<{
  added: [name: string];
  /** Nothing is waiting and the user wants the other ways. */
  usePaste: [];
}>();

const LOOK_EVERY_MS = 4000;

const machines = useMachinesStore();
const { foundMachines } = storeToRefs(machines);

const rest = reactive<Record<string, string>>({});
const busy = shallowRef<string | null>(null);
const failure = reactive<Record<string, { message: string; diagnosis: MachineDiagnosis | null } | undefined>>({});

let timer: ReturnType<typeof setTimeout> | null = null;
let stopped = false;

async function look(): Promise<void> {
  await machines.loadFound();
  if (!stopped) timer = setTimeout(() => { void look(); }, LOOK_EVERY_MS);
}

onMounted(() => { void look(); });
onBeforeUnmount(() => {
  stopped = true;
  if (timer) clearTimeout(timer);
});

/** The code as typed: either the rest after the hint, or the whole code. Null until it is a whole, matching code. */
function codeFor(found: FoundMachine): string | null {
  const typed = (rest[found.machineId] ?? "").replace(/[\s-]/g, "").toUpperCase();
  if (!typed) return null;
  const whole = typed.length >= 8 ? typed : `${found.hint}${typed}`;
  const code = normalizeManualCode(whole);
  return code && code.startsWith(found.hint.replace(/[\s-]/g, "")) ? code : null;
}

function reachLabel(found: FoundMachine): string {
  return found.source === "Tailscale" ? "On Tailscale" : "On your home network";
}

function osLabel(os: string | null): string | null {
  if (!os) return null;
  return ({ linux: "Linux", macos: "macOS", windows: "Windows" } as Record<string, string>)[os.toLowerCase()] ?? os;
}

async function add(found: FoundMachine): Promise<void> {
  const code = codeFor(found);
  if (!code) return;
  busy.value = found.machineId;
  failure[found.machineId] = undefined;
  try {
    const machine = await machines.pairMachine(found.baseUrl, code);
    emit("added", machine.name);
  } catch (error) {
    const diagnosis = diagnosisOf(error);
    failure[found.machineId] = {
      message: diagnosis?.headline ?? (error instanceof Error ? error.message : String(error)),
      diagnosis,
    };
  } finally {
    busy.value = null;
    void machines.loadFound();
  }
}
</script>

<template>
  <div
    class="grid gap-3"
    data-testid="machines-found"
  >
    <p class="text-xs text-muted">
      On the other machine, run <code class="machine-code">fleet node up</code> (or <code class="machine-code">fleet node pair</code>).
      While it waits, it shows up here. Nothing is pasted: type the rest of the code its terminal shows.
    </p>

    <p
      v-if="foundMachines === null"
      class="flex items-center gap-2 text-xs text-muted"
      data-testid="machines-found-looking"
    >
      <Spinner size="sm" />
      Looking on Tailscale and your home network…
    </p>

    <EmptyState
      v-else-if="foundMachines.length === 0"
      :icon="Radar"
      title="No machine is waiting"
      description="Run fleet node up on the other machine. This list updates by itself."
      bordered
      compact
      data-testid="machines-found-empty"
    >
      <template #action>
        <Button
          variant="link"
          size="sm"
          data-testid="machines-found-paste"
          @click="emit('usePaste')"
        >
          Paste a pairing link instead
        </Button>
      </template>
    </EmptyState>

    <ul
      v-else
      class="grid gap-3"
      aria-label="Machines waiting to be added"
    >
      <li
        v-for="found in foundMachines"
        :key="found.machineId"
        class="grid gap-3 rounded-card border border-border bg-card-bg p-3"
        data-testid="machines-found-row"
      >
        <div>
          <p
            class="text-sm font-medium text-text"
            data-testid="machines-found-name"
          >
            {{ [found.name, osLabel(found.os)].filter(Boolean).join(" · ") }}
          </p>
          <p class="mt-0.5 text-xs text-muted">
            {{ reachLabel(found) }}. Waiting to join.
          </p>
        </div>
        <form
          class="flex flex-wrap items-end gap-2"
          @submit.prevent="add(found)"
        >
          <label class="grid gap-1 text-sm text-text">
            <span class="text-xs font-medium text-muted">Check the code in {{ found.name }}'s terminal</span>
            <span class="flex items-center gap-2">
              <span
                class="rounded-btn border border-border bg-main-bg px-3 py-2 font-mono text-sm tracking-widest text-text"
                data-testid="machines-found-hint"
              >{{ found.hint }}-</span>
              <input
                v-model="rest[found.machineId]"
                type="text"
                autocomplete="off"
                autocapitalize="characters"
                spellcheck="false"
                maxlength="9"
                class="w-28 rounded-btn border border-border bg-main-bg px-3 py-2 font-mono text-sm tracking-widest text-text outline-none transition-colors placeholder:text-muted focus:border-accent"
                placeholder="____"
                :aria-label="`The last four characters of the code ${found.name} shows`"
                :disabled="busy === found.machineId"
                data-testid="machines-found-code"
              >
            </span>
          </label>
          <Button
            type="submit"
            :disabled="busy !== null || codeFor(found) === null"
            data-testid="machines-found-add"
          >
            <Spinner
              v-if="busy === found.machineId"
              size="sm"
            />
            {{ busy === found.machineId ? "Checking…" : `Add ${found.name}` }}
          </Button>
        </form>
        <ErrorNote
          v-if="failure[found.machineId]"
          :message="failure[found.machineId]!.message"
          data-testid="machines-found-error"
        >
          <template #details>
            <MachineDiagnosisPanel
              v-if="failure[found.machineId]!.diagnosis"
              :diagnosis="failure[found.machineId]!.diagnosis!"
            />
          </template>
        </ErrorNote>
        <MachineAddNote :name="found.name" />
      </li>
    </ul>
  </div>
</template>
