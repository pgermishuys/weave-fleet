<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, shallowRef } from "vue";
import { storeToRefs } from "pinia";
import { Check, Copy, TriangleAlert } from "lucide-vue-next";
import { Button } from "@/components/ui/button";
import { ErrorNote } from "@/components/ui/error-note";
import { Spinner } from "@/components/ui/spinner";
import { formatCountdown, toTimestamp } from "@/lib/format";
import { useMachinesStore, type JoinCode } from "@/stores/machines";

/**
 * Add a machine → On a new server: one line to paste on a server that has no Fleet yet. The line installs Fleet, starts it
 * as a machine with no screen and joins this Fleet with a one-time code (30 minutes, one use) made here. The code carries
 * every address of this machine, so nobody types one. Nothing is checked by hand: the new machine shows up in the list.
 */
const emit = defineEmits<{
  /** A new machine joined with this line. */
  joined: [name: string];
}>();

/** Where `install.sh` is published with each release. */
const INSTALL_URL = "https://github.com/pgermishuys/weave-fleet/releases/latest/download/install.sh";
const LOOK_EVERY_MS = 3000;

const machines = useMachinesStore();
const { connections } = storeToRefs(machines);

const code = shallowRef<JoinCode | null>(null);
const failure = shallowRef<string | null>(null);
const making = shallowRef(false);
const copied = shallowRef(false);
const now = shallowRef(Date.now());
/** The machines this Fleet had when the code was made: whatever shows up after that is the one that joined. */
let knownIds = new Set<string>();

let tick: ReturnType<typeof setInterval> | null = null;
let look: ReturnType<typeof setTimeout> | null = null;
let copiedTimer: ReturnType<typeof setTimeout> | null = null;
let stopped = false;

const line = computed(() => code.value ? `curl -fsSL ${INSTALL_URL} | sh -s -- --join '${code.value.target}'` : "");
const leftMs = computed(() => code.value ? toTimestamp(code.value.expiresAt) - now.value : 0);
const expired = computed(() => code.value !== null && leftMs.value <= 0);

async function make(): Promise<void> {
  making.value = true;
  failure.value = null;
  copied.value = false;
  try {
    await machines.syncFromServer();
    knownIds = new Set(connections.value.map((machine) => machine.id));
    code.value = await machines.makeJoinCode();
    now.value = Date.now();
    scheduleLook();
  } catch (error) {
    code.value = null;
    failure.value = error instanceof Error ? error.message : String(error);
  } finally {
    making.value = false;
  }
}

function scheduleLook(): void {
  if (look) clearTimeout(look);
  look = setTimeout(() => { void lookForJoined(); }, LOOK_EVERY_MS);
}

async function lookForJoined(): Promise<void> {
  if (stopped || !code.value || expired.value) return;
  await machines.syncFromServer();
  const arrived = connections.value.find((machine) => !knownIds.has(machine.id));
  if (arrived) {
    emit("joined", arrived.name);
    return;
  }
  if (!stopped) scheduleLook();
}

async function copy(): Promise<void> {
  try {
    await navigator.clipboard.writeText(line.value);
    copied.value = true;
    if (copiedTimer) clearTimeout(copiedTimer);
    copiedTimer = setTimeout(() => { copied.value = false; }, 2000);
  } catch {
    copied.value = false;
  }
}

onMounted(() => {
  tick = setInterval(() => { now.value = Date.now(); }, 1000);
  void make();
});
onBeforeUnmount(() => {
  stopped = true;
  if (tick) clearInterval(tick);
  if (look) clearTimeout(look);
  if (copiedTimer) clearTimeout(copiedTimer);
});
</script>

<template>
  <div
    class="grid gap-3"
    data-testid="machines-new-server"
  >
    <p class="text-xs text-muted">
      Paste this on the new machine, in a terminal or a cloud server's start-up script. It installs Fleet, keeps it running, and adds it here.
    </p>

    <p
      v-if="making && !code"
      class="flex items-center gap-2 text-xs text-muted"
      data-testid="machines-new-server-making"
    >
      <Spinner size="sm" />
      Making a join code…
    </p>

    <ErrorNote
      v-else-if="failure"
      message="Couldn't make a join code."
      :reason="failure"
      :retrying="making"
      data-testid="machines-new-server-error"
      @retry="make"
    />

    <template v-else-if="code">
      <div
        class="flex items-start gap-2 rounded-card border border-border bg-card-bg p-3"
        :class="{ 'opacity-60': expired }"
      >
        <code
          class="min-w-0 flex-1 break-all font-mono text-xs text-text"
          data-testid="machines-new-server-line"
        >{{ line }}</code>
        <Button
          type="button"
          variant="outline"
          size="sm"
          :disabled="expired"
          data-testid="machines-new-server-copy"
          @click="copy"
        >
          <Check
            v-if="copied"
            :size="14"
            aria-hidden="true"
          />
          <Copy
            v-else
            :size="14"
            aria-hidden="true"
          />
          {{ copied ? "Copied" : "Copy" }}
        </Button>
      </div>

      <p
        class="flex items-start gap-2 text-xs text-muted"
        data-testid="machines-new-server-warning"
      >
        <TriangleAlert
          :size="14"
          class="mt-0.5 shrink-0"
          aria-hidden="true"
        />
        <span>
          Works once, for 30 minutes. Anyone who has the line can add a machine to {{ code.machineName }}, so treat it like a password.
        </span>
      </p>

      <p
        v-if="expired"
        class="flex flex-wrap items-center gap-2 text-sm text-text"
        data-testid="machines-new-server-expired"
      >
        This code has expired.
        <Button
          type="button"
          variant="outline"
          size="sm"
          :disabled="making"
          data-testid="machines-new-server-renew"
          @click="make"
        >
          Make a new code
        </Button>
      </p>
      <p
        v-else
        class="flex items-center gap-2 text-xs text-muted"
        data-testid="machines-new-server-waiting"
      >
        <Spinner size="xs" />
        Waiting for a machine to join… The code expires {{ formatCountdown(leftMs) }}.
      </p>
    </template>
  </div>
</template>
