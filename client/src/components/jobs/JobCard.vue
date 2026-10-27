<script setup lang="ts">
import type { Job } from "@/lib/jobs";

defineProps<{ job: Job; selected: boolean }>();
</script>

<template>
  <button
    type="button"
    class="w-full rounded-card border bg-main-bg p-3 text-left transition-colors hover:border-accent/50"
    :class="selected ? 'border-accent' : 'border-border'"
    data-testid="job-card"
    :data-state="job.state"
  >
    <p class="text-sm font-medium text-text">
      {{ job.title }}
    </p>
    <p class="mt-1 flex flex-wrap items-center gap-1.5 text-xs text-muted">
      <span
        v-if="job.machineName"
        class="font-mono text-text"
        data-testid="job-machine"
      >{{ job.machineName }}</span>
      <span
        v-for="label in job.labels"
        :key="label"
        class="rounded border border-border px-1.5 py-px font-mono"
      >needs {{ label }}</span>
      <span v-if="job.state === 'failed'">Failed</span>
    </p>
    <p
      v-if="job.whyWaiting"
      class="mt-2 border-t border-dashed border-border pt-2 text-xs text-muted"
      data-testid="job-why-waiting"
    >
      {{ job.whyWaiting }}
    </p>
    <p
      v-else-if="job.state === 'needs_you'"
      class="mt-2 text-xs text-accent"
    >
      Waiting for your answer
    </p>
    <p
      v-else-if="job.error"
      class="mt-2 text-xs text-muted"
    >
      {{ job.error }}
    </p>
  </button>
</template>
