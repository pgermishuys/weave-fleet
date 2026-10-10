<script setup lang="ts">
import { computed } from "vue";
import { Check, Circle } from "lucide-vue-next";
import { elapsedText, megabytes, progressTitle, type ModsRuntimeJob, type ModsRuntimeRelease } from "@/lib/mods-runtime";
import { SECONDARY_BUTTON } from "./classes";

/**
 * An install running (step 3): Download, Check and Unpack with the megabytes and a bar, and Cancel.
 */
const props = defineProps<{
  job: ModsRuntimeJob;
  release: ModsRuntimeRelease;
  /** Settings was opened after the install started, so say how long it has been going. */
  cameBack?: boolean;
}>();

defineEmits<{ cancel: [] }>();

const total = computed(() => megabytes(props.job.bytesTotal ?? props.release.size));
const received = computed(() => (props.job.phase === "downloading" ? megabytes(props.job.bytesReceived) : total.value));
const sizeText = computed(() => `${received.value} of ${total.value} MB`);
const percent = computed(() => (total.value ? Math.min(100, Math.round((received.value / total.value) * 100)) : 0));

const order = ["downloading", "verifying", "extracting"];
const steps = computed(() => {
  const at = Math.max(0, order.indexOf(props.job.phase));
  return [
    { id: "download", label: "Download" },
    { id: "check", label: "Check" },
    { id: "unpack", label: "Unpack" },
  ].map((step, index) => ({ ...step, status: index < at ? "done" : index === at ? "current" : "todo" }));
});

const footer = computed(() => {
  if (props.cameBack) return `Started ${elapsedText(props.job.startedAt, Date.now())} ago. It kept going while you were away.`;
  return "This keeps going if you leave Settings. Mods start as soon as it's done.";
});
</script>

<template>
  <section
    aria-labelledby="mods-progress-heading"
    class="space-y-3"
  >
    <div class="flex flex-wrap items-baseline justify-between gap-x-4 gap-y-1">
      <h4
        id="mods-progress-heading"
        class="text-sm font-medium text-text"
      >
        {{ progressTitle(job) }}
      </h4>
      <p class="text-sm text-muted">
        {{ sizeText }}
      </p>
    </div>

    <div
      role="progressbar"
      :aria-label="progressTitle(job)"
      aria-valuemin="0"
      :aria-valuenow="received"
      :aria-valuemax="total"
      :aria-valuetext="sizeText"
      class="h-1.5 overflow-hidden rounded-full bg-border"
    >
      <div
        class="h-full rounded-full bg-accent transition-[width] duration-300"
        :style="{ width: `${percent}%` }"
      />
    </div>

    <div class="flex flex-wrap items-center justify-between gap-3">
      <ol class="flex flex-wrap items-center gap-x-4 gap-y-1 text-sm">
        <li
          v-for="step in steps"
          :key="step.id"
          :data-testid="`mods-step-${step.id}`"
          :data-status="step.status"
          :aria-current="step.status === 'current' ? 'step' : undefined"
          class="flex items-center gap-1.5"
          :class="step.status === 'done' ? 'text-running' : step.status === 'current' ? 'text-text' : 'text-muted'"
        >
          <Check
            v-if="step.status === 'done'"
            :size="14"
            aria-hidden="true"
          />
          <Circle
            v-else
            :size="10"
            :fill="step.status === 'current' ? 'currentColor' : 'none'"
            aria-hidden="true"
          />
          {{ step.label }}
        </li>
      </ol>
      <button
        type="button"
        :class="SECONDARY_BUTTON"
        @click="$emit('cancel')"
      >
        Cancel
      </button>
    </div>

    <p
      v-if="footer"
      class="max-w-xl text-sm leading-6 text-muted"
    >
      {{ footer }}
    </p>
  </section>
</template>
