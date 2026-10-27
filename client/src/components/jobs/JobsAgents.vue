<script setup lang="ts">
import { computed } from "vue";
import type { Job, JobMachine } from "@/lib/jobs";

const props = defineProps<{ machines: readonly JobMachine[]; jobs: readonly Job[] }>();

/** What each machine's seats hold: the jobs there that run or need the user, and how many seats are free. */
const cards = computed(() => props.machines.map((machine) => {
  const holding = props.jobs.filter((job) => job.machineId === machine.id && (job.state === "running" || job.state === "needs_you"));
  return { machine, holding, free: Math.max(machine.seats - holding.length, 0) };
}));
</script>

<template>
  <div
    class="grid grid-cols-1 gap-4 overflow-y-auto p-5 lg:grid-cols-2"
    data-testid="jobs-agents"
  >
    <section
      v-for="card in cards"
      :key="card.machine.id"
      class="rounded-card border border-border bg-main-bg p-4"
      :class="card.machine.seats === 0 ? 'opacity-60' : ''"
      data-testid="jobs-agent-card"
    >
      <header class="flex items-baseline gap-2">
        <h3 class="font-mono text-sm font-semibold text-text">
          {{ card.machine.name }}
        </h3>
        <span class="text-xs text-muted">
          {{ card.machine.isLocal ? "this machine · " : "" }}{{ card.holding.length }} of {{ card.machine.seats }} seats
        </span>
      </header>
      <p
        v-if="card.machine.labels.length"
        class="mt-2 flex flex-wrap gap-1.5"
      >
        <span
          v-for="label in card.machine.labels"
          :key="label"
          class="rounded border border-border px-1.5 py-px font-mono text-xs text-muted"
        >{{ label }}</span>
      </p>
      <p
        v-if="card.machine.seats === 0"
        class="mt-3 text-xs text-muted"
      >
        Takes no jobs. Give it seats in Settings → Machines.
      </p>
      <ul
        v-else
        class="mt-3 space-y-1.5"
      >
        <li
          v-for="job in card.holding"
          :key="job.id"
          class="flex items-center justify-between rounded-btn bg-border/30 px-3 py-2 text-sm text-text"
        >
          <span>{{ job.title }}</span>
          <span class="text-xs text-muted">{{ job.state === "needs_you" ? "needs you" : "running" }}</span>
        </li>
        <li
          v-for="n in card.free"
          :key="`free-${n}`"
          class="rounded-btn border border-dashed border-border px-3 py-2 text-xs text-muted"
        >
          Free · takes the next job that fits
        </li>
      </ul>
    </section>
  </div>
</template>
