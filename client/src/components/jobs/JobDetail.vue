<script setup lang="ts">
import { Button } from "@/components/ui/button";
import type { Job } from "@/lib/jobs";

defineProps<{ job: Job; machineName: (id: string | null) => string | null }>();
defineEmits<{ open: []; cancel: [] }>();
</script>

<template>
  <aside
    class="flex w-80 shrink-0 flex-col gap-4 overflow-y-auto border-l border-border p-5"
    data-testid="job-detail"
  >
    <div>
      <p class="text-xs text-muted">
        {{ job.machineName ? `${job.state === "needs_you" ? "Needs you" : job.state === "done" ? "Done" : job.state === "failed" ? "Failed" : "Running"} on ${job.machineName}` : "Waiting" }}
      </p>
      <h2 class="mt-1 text-lg font-semibold text-text">
        {{ job.title }}
      </h2>
    </div>

    <p class="whitespace-pre-wrap rounded-card border border-border bg-main-bg p-3 text-sm text-text">
      {{ job.ask }}
    </p>

    <dl class="grid grid-cols-[auto_1fr] gap-x-4 gap-y-2 text-sm">
      <dt class="text-muted">
        Repository
      </dt>
      <dd class="break-all font-mono text-xs text-text">
        {{ job.repository }}
      </dd>
      <template v-if="job.labels.length">
        <dt class="text-muted">
          Needs
        </dt>
        <dd class="text-text">
          {{ job.labels.join(", ") }}
        </dd>
      </template>
      <template v-if="job.pinnedMachineId">
        <dt class="text-muted">
          Run on
        </dt>
        <dd class="text-text">
          {{ machineName(job.pinnedMachineId) ?? "A machine that is no longer listed" }}
        </dd>
      </template>
      <template v-if="job.whyWaiting || job.error">
        <dt class="text-muted">
          Why
        </dt>
        <dd
          class="text-text"
          data-testid="job-detail-why"
        >
          {{ job.whyWaiting ?? job.error }}
        </dd>
      </template>
    </dl>

    <div class="mt-auto flex gap-2">
      <Button
        v-if="job.sessionId"
        size="sm"
        data-testid="job-open-session"
        @click="$emit('open')"
      >
        Open session
      </Button>
      <Button
        v-if="job.state === 'waiting'"
        size="sm"
        variant="outline"
        data-testid="job-cancel"
        @click="$emit('cancel')"
      >
        Cancel
      </Button>
    </div>
  </aside>
</template>
