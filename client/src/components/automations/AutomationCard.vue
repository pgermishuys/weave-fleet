<script setup lang="ts">
import { Play, Pause, Edit, Trash2 } from "lucide-vue-next";
import type { Automation } from "@/composables/use-automations";

defineProps<{
  automation: Automation;
}>();

defineEmits<{
  play: [id: string];
  pause: [id: string];
  edit: [id: string];
  delete: [id: string];
}>();
</script>

<template>
  <article class="rounded-card border border-border bg-main-bg p-4">
    <div class="flex items-start justify-between gap-4">
      <div class="flex-1 space-y-3">
        <!-- Title and badge -->
        <div class="flex items-center gap-3">
          <h3 class="font-bold text-text">{{ automation.name }}</h3>
          <span
            v-if="automation.isEnabled"
            class="border border-green-600 px-2 py-0.5 text-xs font-medium text-green-600"
          >
            Enabled
          </span>
          <span
            v-else
            class="border border-muted px-2 py-0.5 text-xs font-medium text-muted"
          >
            Disabled
          </span>
        </div>

        <!-- Prompt -->
        <div class="space-y-1">
          <p class="text-xs font-semibold uppercase tracking-[0.12em] text-muted">
            Prompt
          </p>
          <p class="text-sm text-text">{{ automation.prompt }}</p>
        </div>

        <!-- Trigger -->
        <div class="space-y-1">
          <p class="text-xs font-semibold uppercase tracking-[0.12em] text-muted">
            Trigger
          </p>
          <p class="text-sm text-text">
            {{ automation.triggerType === "schedule" ? "Schedule" : "Event" }}:
            <code class="font-mono text-sm">{{ automation.triggerConfig }}</code>
          </p>
        </div>

        <!-- Policy -->
        <div class="space-y-1">
          <p class="text-xs font-semibold uppercase tracking-[0.12em] text-muted">
            Policy
          </p>
          <p class="text-sm text-text">
            Max {{ automation.maxConcurrentRuns }} concurrent,
            {{ automation.maxRunsPerHour }}/hour,
            {{ automation.timeoutMinutes }}min timeout
          </p>
        </div>

        <!-- Metadata badges -->
        <div class="flex flex-wrap items-center gap-2">
          <span
            v-if="automation.workspaceId"
            class="border border-border bg-card-bg px-2 py-0.5 text-xs font-medium text-muted"
            :title="`Workspace: ${automation.workspaceId}`"
          >
            Workspace: {{ automation.workspaceId }}
          </span>
          <span
            v-if="automation.model"
            class="border border-border bg-card-bg px-2 py-0.5 text-xs font-medium text-muted"
            :title="`Model: ${automation.model}`"
          >
            Model: {{ automation.model }}
          </span>
          <span
            v-if="automation.agent"
            class="border border-border bg-card-bg px-2 py-0.5 text-xs font-medium text-muted"
            :title="`Agent: ${automation.agent}`"
          >
            Agent: {{ automation.agent }}
          </span>
          <span
            class="border border-border bg-card-bg px-2 py-0.5 text-xs font-medium text-muted"
            :title="`Created: ${new Date(automation.createdAt).toLocaleString()}`"
          >
            Created: {{ new Date(automation.createdAt).toLocaleDateString() }}
          </span>
        </div>
      </div>

      <!-- Action icons -->
      <div class="flex items-center gap-2">
        <button
          class="p-2 text-muted transition-colors hover:text-text"
          :title="automation.isEnabled ? 'Run now' : 'Enable automation'"
          @click="$emit('play', automation.id)"
        >
          <Play :size="18" />
        </button>
        <button
          v-if="automation.isEnabled"
          class="p-2 text-muted transition-colors hover:text-text"
          title="Disable automation"
          @click="$emit('pause', automation.id)"
        >
          <Pause :size="18" />
        </button>
        <button
          class="p-2 text-muted transition-colors hover:text-text"
          title="Edit automation"
          @click="$emit('edit', automation.id)"
        >
          <Edit :size="18" />
        </button>
        <button
          class="p-2 text-muted transition-colors hover:text-destructive"
          title="Delete automation"
          @click="$emit('delete', automation.id)"
        >
          <Trash2 :size="18" />
        </button>
      </div>
    </div>
  </article>
</template>
