<script setup lang="ts">
import { computed, onMounted, shallowRef, watch } from "vue";
import { MAX_JOB_SEATS, parseLabels } from "@/lib/jobs";
import { HOME_MACHINE_KEY } from "@/lib/machines";
import { useJobsStore } from "@/stores/jobs";

/** A machine's seats and labels for Jobs, on its card in Settings → Machines. Only drawn with Jobs on. */
const props = defineProps<{ entryKey: string }>();

const store = useJobsStore();
const inputClass = "rounded-btn border border-border bg-main-bg px-2 py-1 text-sm text-text outline-none focus:border-accent";

onMounted(() => {
  if (!store.isLoaded) void store.load();
});

// The home card is this Fleet's own machine; another card is named by its id.
const machine = computed(() => store.machines.find((candidate) =>
  props.entryKey === HOME_MACHINE_KEY ? candidate.isLocal : candidate.id === props.entryKey));

const seats = shallowRef(0);
const labels = shallowRef("");
const errorMessage = shallowRef<string | null>(null);

watch(machine, (found) => {
  seats.value = found?.seats ?? 0;
  labels.value = found?.labels.join(", ") ?? "";
}, { immediate: true });

async function save(change: { seats?: number; labels?: string[] }): Promise<void> {
  if (!machine.value) return;
  errorMessage.value = null;
  try {
    await store.saveMachine(machine.value.id, change);
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : "Couldn't save.";
  }
}

function saveSeats(): Promise<void> {
  seats.value = Math.min(Math.max(Math.trunc(seats.value) || 0, 0), MAX_JOB_SEATS);
  return save({ seats: seats.value });
}

function saveLabels(): Promise<void> {
  return save({ labels: parseLabels(labels.value) });
}
</script>

<template>
  <div
    v-if="machine"
    class="mt-2 flex flex-wrap items-center gap-3 text-sm text-muted"
    data-testid="machine-job-seats"
  >
    <label class="flex items-center gap-2">
      Seats for jobs
      <input
        v-model.number="seats"
        type="number"
        min="0"
        :max="MAX_JOB_SEATS"
        :class="inputClass"
        class="w-16"
        data-testid="machine-job-seats-input"
        @change="saveSeats"
      >
    </label>
    <label class="flex items-center gap-2">
      Labels
      <input
        v-model="labels"
        type="text"
        placeholder="none"
        :class="inputClass"
        class="w-40"
        data-testid="machine-job-labels-input"
        @change="saveLabels"
      >
    </label>
    <span
      v-if="errorMessage"
      class="text-red-400"
      role="alert"
    >{{ errorMessage }}</span>
  </div>
</template>
