<script setup lang="ts">
import { computed } from "vue";
import { LoaderCircle, RefreshCw } from "lucide-vue-next";
import { useFleetUpdates } from "@/composables/use-fleet-updates";
import { useWhatsNew } from "@/composables/use-whats-new";
import type { FleetUpdateRow } from "@/lib/fleet-updates";

/**
 * Settings → Updates: every Fleet you run, with its version and where it is with updating. A Fleet downloads a release
 * itself and switches to it when it restarts; Restart does that now, one machine or (Update machines now) every machine
 * that has one waiting, one at a time. A machine whose new version doesn't start goes back to the old one by itself.
 */

const {
  rows, summary, isBusy, isChecking, checkNow, openDownload,
  rolloutRunning, outcome, readyMachines, actionError, restartingSelf, startRollout, stopRollout, restart,
} = useFleetUpdates();
const { openWhatsNew } = useWhatsNew();

const apps = computed(() => rows.value.filter((row) => row.kind === "app"));
const fleets = computed(() => rows.value.filter((row) => row.kind !== "app"));

const headline = computed(() => {
  const { latest, waiting, downloaded } = summary.value;
  if (!latest) return "Fleet hasn't found a release yet. Check now asks every Fleet below.";
  if (!waiting) return `Fleet ${latest} is out. Everything that can be reached is on it.`;
  const have = downloaded === waiting
    ? `${waiting === 1 ? "The Fleet" : "Every Fleet"} below that can update has downloaded it.`
    : `${downloaded} of ${waiting} Fleets that can update have downloaded it.`;
  return `Fleet ${latest} is out. ${have} They switch to it when they restart.`;
});

const counts = computed(() => {
  const { waiting, unreachable } = summary.value;
  const parts = [`${waiting} of ${rows.value.length} can update`];
  if (unreachable) parts.push(`${unreachable} can't be reached`);
  return parts.join(" · ");
});

const dotClass: Record<FleetUpdateRow["state"]["tone"], string> = {
  ok: "bg-[var(--running)]",
  warn: "bg-[var(--idle)]",
  info: "bg-[var(--complete)]",
  bad: "bg-[var(--error)]",
  muted: "bg-muted",
};
const stateClass: Record<FleetUpdateRow["state"]["tone"], string> = {
  ok: "text-[var(--running)]",
  warn: "text-[var(--idle)]",
  info: "text-[var(--complete)]",
  bad: "text-[var(--error)]",
  muted: "text-muted",
};

const outcomeClass: Record<FleetUpdateRow["state"]["tone"], string> = {
  ok: "border-[var(--running)]/40 text-[var(--running)]",
  warn: "border-[var(--idle)]/40 text-[var(--idle)]",
  info: "border-[var(--complete)]/40 text-[var(--complete)]",
  bad: "border-[var(--error)]/40 text-[var(--error)]",
  muted: "border-border text-muted",
};

const KIND_LABELS = { app: "desktop app", fleet: "Fleet", node: "node" } as const;
</script>

<template>
  <section
    class="rounded-card border border-border bg-card-bg p-6 shadow-sm"
    data-testid="updates-section"
  >
    <div class="flex flex-col gap-3 md:flex-row md:items-start md:justify-between">
      <div class="space-y-1">
        <h2 class="text-lg font-semibold text-text">
          Updates
        </h2>
        <p
          class="max-w-prose text-sm text-muted"
          data-testid="updates-headline"
        >
          {{ headline }}
          <button
            v-if="summary.latest"
            type="button"
            class="underline underline-offset-2 transition-colors hover:text-text"
            data-testid="updates-whats-new"
            @click="openWhatsNew(summary.latest)"
          >
            What's new
          </button>
        </p>
      </div>
    </div>

    <div class="mt-5 flex flex-wrap items-center justify-between gap-3">
      <p
        class="font-mono text-xs text-muted"
        data-testid="updates-summary"
      >
        {{ counts }}
      </p>
      <div class="flex flex-wrap items-center gap-2">
        <button
          type="button"
          :disabled="isChecking || rolloutRunning"
          class="inline-flex items-center justify-center gap-2 rounded-btn border border-border bg-main-bg px-3 py-1.5 text-sm font-medium text-text transition-colors hover:border-accent/50 disabled:cursor-not-allowed disabled:opacity-60"
          data-testid="updates-check-now"
          @click="checkNow"
        >
          <RefreshCw
            :size="14"
            :class="{ 'animate-spin': isChecking }"
            aria-hidden="true"
          />
          Check now
        </button>
        <button
          v-if="rolloutRunning"
          type="button"
          class="inline-flex items-center justify-center rounded-btn border border-border bg-main-bg px-3 py-1.5 text-sm font-medium text-text transition-colors hover:border-accent/50"
          data-testid="updates-stop-rollout"
          @click="stopRollout"
        >
          Stop after this one
        </button>
        <button
          type="button"
          :disabled="rolloutRunning || !readyMachines"
          class="inline-flex items-center justify-center gap-2 rounded-btn bg-accent px-3 py-1.5 text-sm font-medium text-white transition-opacity hover:opacity-90 disabled:cursor-not-allowed disabled:opacity-50"
          data-testid="updates-update-machines"
          @click="startRollout()"
        >
          <LoaderCircle
            v-if="rolloutRunning"
            :size="14"
            class="animate-spin"
            aria-hidden="true"
          />
          {{ rolloutRunning ? "Updating…" : readyMachines ? `Update machines now (${readyMachines})` : "Machines up to date" }}
        </button>
      </div>
    </div>

    <p
      v-if="outcome && !rolloutRunning"
      class="mt-3 rounded-btn border px-3 py-2 font-mono text-[12px]"
      :class="outcomeClass[outcome.tone]"
      data-testid="updates-outcome"
    >
      {{ outcome.text }}
    </p>
    <p
      v-if="actionError"
      class="mt-3 rounded-btn border border-[var(--error)]/40 px-3 py-2 font-mono text-[12px] text-[var(--error)]"
      data-testid="updates-error"
    >
      {{ actionError }}
    </p>

    <template
      v-for="group in [{ title: 'Apps on your devices', list: apps }, { title: 'Machines', list: fleets }]"
      :key="group.title"
    >
      <template v-if="group.list.length">
        <h3 class="mb-2 mt-6 text-[10px] font-semibold uppercase tracking-wider text-muted">
          {{ group.title }}
        </h3>
        <ul
          class="grid gap-2"
          :aria-label="group.title"
        >
          <li
            v-for="row in group.list"
            :key="row.key"
            class="flex flex-wrap items-start gap-3 rounded-card border border-border bg-main-bg px-4 py-3"
            data-testid="update-row"
            :data-fleet="row.key"
            :data-state="row.state.kind"
          >
            <span
              class="mt-1.5 h-2 w-2 shrink-0 rounded-full"
              :class="dotClass[row.state.tone]"
              aria-hidden="true"
            />
            <div class="flex min-w-0 flex-1 flex-col gap-0.5">
              <span class="flex flex-wrap items-center gap-2 font-mono text-[13.5px] font-semibold text-text">
                {{ row.name }}
                <span class="rounded border border-border px-1.5 font-mono text-[10px] font-semibold uppercase tracking-wider text-muted">{{ KIND_LABELS[row.kind] }}</span>
                <span
                  v-if="row.os"
                  class="rounded border border-border px-1.5 font-mono text-[10px] font-semibold uppercase tracking-wider text-muted"
                >{{ row.os }}</span>
                <span
                  v-if="row.isLive"
                  class="rounded bg-[color-mix(in_srgb,var(--coral)_14%,transparent)] px-1.5 py-px text-[9.5px] font-bold uppercase tracking-widest text-[var(--coral)]"
                >Live</span>
              </span>
              <span class="break-all font-mono text-[11.5px] text-muted">
                {{ row.where }}
                <template v-if="row.version">
                  · <span data-testid="update-version">{{ row.version }}<template v-if="row.state.target"> → {{ row.state.target }}</template></span>
                </template>
              </span>
              <span
                class="font-mono text-[11.5px]"
                :class="stateClass[row.state.tone]"
                data-testid="update-state"
              >
                <LoaderCircle
                  v-if="['downloading', 'checking', 'waiting', 'restarting'].includes(row.state.kind)"
                  :size="11"
                  class="mr-1 inline animate-spin"
                  aria-hidden="true"
                />{{ row.state.label }}<template v-if="row.state.kind === 'unreachable' && row.version"> · last seen on {{ row.version }}</template>
              </span>
            </div>
            <button
              v-if="row.restart?.kind === 'button'"
              type="button"
              :disabled="row.restart.disabled || rolloutRunning || !!restartingSelf"
              class="inline-flex items-center rounded-btn border border-border bg-card-bg px-3 py-1.5 text-xs font-medium text-text transition-colors hover:border-accent/50 disabled:cursor-not-allowed disabled:opacity-60"
              data-testid="update-restart"
              @click="restart(row)"
            >
              {{ row.restart.label }}
            </button>
            <span
              v-else-if="row.restart?.kind === 'unavailable'"
              class="max-w-[16rem] text-right font-mono text-[11px] text-muted"
              data-testid="update-restart-unavailable"
            >
              {{ row.restart.reason }}
            </span>
            <button
              v-if="row.state.kind === 'manual'"
              type="button"
              class="inline-flex items-center rounded-btn border border-border bg-card-bg px-3 py-1.5 text-xs font-medium text-text transition-colors hover:border-accent/50"
              data-testid="update-download"
              @click="openDownload()"
            >
              Download {{ row.state.target }}
            </button>
          </li>
        </ul>
      </template>
    </template>

    <p
      v-if="isBusy && !isChecking"
      class="mt-4 text-xs text-muted"
    >
      Downloading… this page updates as it goes.
    </p>
  </section>
</template>
