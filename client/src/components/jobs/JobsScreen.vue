<script setup lang="ts">
import { computed, onMounted, onUnmounted, shallowRef } from "vue";
import { useRouter } from "@tanstack/vue-router";
import { Plus } from "lucide-vue-next";
import { Button } from "@/components/ui/button";
import JobCard from "@/components/jobs/JobCard.vue";
import JobDetail from "@/components/jobs/JobDetail.vue";
import JobsAgents from "@/components/jobs/JobsAgents.vue";
import NewJobDialog from "@/components/jobs/NewJobDialog.vue";
import { jobColumns, type Job } from "@/lib/jobs";
import { HOME_MACHINE_KEY } from "@/lib/machines";
import { useJobsStore } from "@/stores/jobs";
import { useMachinesStore } from "@/stores/machines";

/** The Jobs screen: what's queued, running and finished across the machines, and each machine's seats. */
const store = useJobsStore();
const machinesStore = useMachinesStore();
const router = useRouter();

const tab = shallowRef<"queue" | "agents">("queue");
const selectedId = shallowRef<string | null>(null);
const isNewOpen = shallowRef(false);
const actionError = shallowRef<string | null>(null);

const columns = computed(() => jobColumns(store.jobs));
const selected = computed(() => store.jobs.find((job) => job.id === selectedId.value) ?? null);
// Seats are given to machines, so a machine with none is left out of the bar.
const seated = computed(() => store.machines.filter((machine) => machine.seats > 0));

let stopWatching: (() => void) | null = null;
onMounted(() => {
  stopWatching = store.watch();
});
onUnmounted(() => stopWatching?.());

function machineName(id: string | null): string | null {
  return store.machines.find((machine) => machine.id === id)?.name ?? null;
}

/** The session is on the machine that took the job: this one, or another that goes live to show it. */
function openSession(job: Job): void {
  if (!job.sessionId) return;
  const machine = store.machines.find((candidate) => candidate.id === job.machineId);
  const key = machine && !machine.isLocal ? machine.id : HOME_MACHINE_KEY;
  if (key === machinesStore.liveKey) {
    void router.navigate({ to: "/sessions/$id", params: { id: job.sessionId }, search: { instanceId: undefined, parentSessionId: undefined } });
    return;
  }

  machinesStore.openOn(key, `/sessions/${encodeURIComponent(job.sessionId)}`);
}

async function cancel(job: Job): Promise<void> {
  actionError.value = null;
  try {
    await store.cancel(job.id);
    selectedId.value = null;
  } catch (error) {
    actionError.value = error instanceof Error ? error.message : "Couldn't cancel the job.";
  }
}
</script>

<template>
  <section
    class="flex h-full flex-col overflow-hidden"
    data-testid="jobs-screen"
  >
    <header class="flex flex-wrap items-center gap-4 border-b border-border px-5 py-3">
      <h1 class="text-lg font-semibold text-text">
        Jobs
      </h1>
      <div
        class="flex rounded-btn border border-border p-0.5 text-sm"
        role="tablist"
      >
        <button
          v-for="name in (['queue', 'agents'] as const)"
          :key="name"
          type="button"
          role="tab"
          :aria-selected="tab === name"
          class="rounded-btn px-3 py-1 capitalize transition-colors"
          :class="tab === name ? 'bg-border/60 text-text' : 'text-muted hover:text-text'"
          :data-testid="`jobs-tab-${name}`"
          @click="tab = name"
        >
          {{ name }}
        </button>
      </div>
      <ul
        class="ml-auto flex flex-wrap items-center gap-2"
        data-testid="jobs-seats"
      >
        <li
          v-for="machine in seated"
          :key="machine.id"
          class="rounded-full border border-border px-3 py-1 font-mono text-xs text-text"
          :class="machine.status !== 'online' ? 'opacity-50' : ''"
          data-testid="jobs-seat-chip"
        >
          {{ machine.name }} {{ machine.used }}/{{ machine.seats }}
        </li>
      </ul>
      <Button
        size="sm"
        data-testid="jobs-new"
        @click="isNewOpen = true"
      >
        <Plus :size="16" />
        New job
      </Button>
    </header>

    <p
      v-if="store.error"
      class="px-5 py-2 text-sm text-red-400"
      role="alert"
      data-testid="jobs-error"
    >
      {{ store.error }}
    </p>

    <div class="flex min-h-0 flex-1">
      <template v-if="tab === 'queue'">
        <div
          class="grid min-w-0 flex-1 grid-cols-4 gap-4 overflow-x-auto p-5"
          data-testid="jobs-queue"
        >
          <section
            v-for="column in columns"
            :key="column.id"
            class="flex min-w-48 flex-col gap-3"
            :data-testid="`jobs-column-${column.id}`"
          >
            <h2 class="text-sm font-medium text-text">
              {{ column.title }}
              <span class="text-muted">{{ column.jobs.length }}</span>
            </h2>
            <JobCard
              v-for="job in column.jobs"
              :key="job.id"
              :job="job"
              :selected="job.id === selectedId"
              @click="selectedId = job.id"
            />
          </section>
          <p
            v-if="store.isLoaded && store.jobs.length === 0"
            class="col-span-4 text-sm text-muted"
            data-testid="jobs-empty"
          >
            No jobs yet. Post one with New job, and Fleet starts it on a machine with a free seat.
          </p>
        </div>
        <JobDetail
          v-if="selected"
          :job="selected"
          :machine-name="machineName"
          @open="openSession(selected)"
          @cancel="cancel(selected)"
        />
      </template>
      <JobsAgents
        v-else
        class="flex-1"
        :machines="store.machines"
        :jobs="store.jobs"
      />
    </div>

    <p
      v-if="actionError"
      class="px-5 py-2 text-sm text-red-400"
      role="alert"
    >
      {{ actionError }}
    </p>
    <NewJobDialog
      v-model:open="isNewOpen"
      :machines="store.machines"
    />
  </section>
</template>
