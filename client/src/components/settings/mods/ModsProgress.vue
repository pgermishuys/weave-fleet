<script setup lang="ts">
import { computed } from "vue";
import { Check, Circle } from "lucide-vue-next";
import { elapsedText, megabytes, progressTitle, type ModsRuntimeJob, type ModsRuntimeRelease } from "@/lib/mods-runtime";
import { SECONDARY_BUTTON } from "./classes";

/**
 * An install running (steps 3, 8 and 9): Download, Check and Unpack with the megabytes and a bar. Accent for the
 * first install and updates, amber for a security fix. Only the first install can be cancelled: an update runs while
 * mods keep working on the Bun they have.
 */
const props = defineProps<{
  job: ModsRuntimeJob;
  release: ModsRuntimeRelease;
  /** Settings was opened after the install started, so say how long it has been going. */
  cameBack?: boolean;
}>();

defineEmits<{ cancel: [] }>();

const security = computed(() => props.job.kind === "security");
const total = computed(() => megabytes(props.job.bytesTotal ?? props.release.size));
const received = computed(() => (props.job.phase === "downloading" ? megabytes(props.job.bytesReceived) : total.value));
const sizeText = computed(() => `${received.value} of ${total.value} MB`);
const percent = computed(() => {
  if (props.job.phase !== "downloading") return 100;
  const totalBytes = props.job.bytesTotal ?? props.release.size ?? 0;
  return totalBytes > 0 ? Math.min(100, Math.round(((props.job.bytesReceived ?? 0) / totalBytes) * 100)) : 0;
});

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
  const { job } = props;
  if (job.kind === "security") {
    return `Bun ${job.from} has a security problem that ${job.version} fixes, so Fleet is installing it now. Mods keep running meanwhile.`;
  }
  if (job.kind === "update") {
    return `Mods keep running on Bun ${job.from} until it's done, then move over. Fleet looks for a new Bun when it starts and every 4 hours.`;
  }
  if (props.cameBack) return `Started ${elapsedText(job.startedAt, Date.now())} ago. It kept going while you were away.`;
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
        class="text-sm font-medium"
        :class="security ? 'text-idle' : 'text-text'"
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
        class="h-full rounded-full transition-[width] duration-300"
        :class="security ? 'bg-idle' : 'bg-accent'"
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
        v-if="job.kind === 'install'"
        type="button"
        :class="SECONDARY_BUTTON"
        @click="$emit('cancel')"
      >
        Cancel
      </button>
    </div>

    <p class="max-w-xl text-sm leading-6 text-muted">
      {{ footer }}
    </p>
  </section>
</template>
