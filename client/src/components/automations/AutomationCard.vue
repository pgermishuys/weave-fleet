<script setup lang="ts">
import { Play, Pause, Edit, Trash2 } from "lucide-vue-next";

export interface Automation {
  id: string;
  title: string;
  enabled: boolean;
  prompt: string;
  trigger: {
    type: "schedule" | "event";
    value: string;
  };
  policy: {
    maxConcurrent: number;
    maxPerHour: number;
    timeoutMinutes: number;
  };
}

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
  <article class="border-b border-border py-4 first:pt-0 last:border-b-0">
    <div class="flex items-start justify-between gap-4">
      <div class="flex-1 space-y-3">
        <!-- Title and badge -->
        <div class="flex items-center gap-3">
          <h3 class="font-bold text-foreground">{{ automation.title }}</h3>
          <span
            v-if="automation.enabled"
            class="border border-green-600 px-2 py-0.5 text-xs font-medium text-green-600"
          >
            Enabled
          </span>
          <span
            v-else
            class="border border-muted-foreground px-2 py-0.5 text-xs font-medium text-muted-foreground"
          >
            Disabled
          </span>
        </div>

        <!-- Prompt -->
        <div class="space-y-1">
          <p class="text-xs font-semibold uppercase tracking-[0.12em] text-muted-foreground">
            Prompt
          </p>
          <p class="text-sm text-foreground">{{ automation.prompt }}</p>
        </div>

        <!-- Trigger -->
        <div class="space-y-1">
          <p class="text-xs font-semibold uppercase tracking-[0.12em] text-muted-foreground">
            Trigger
          </p>
          <p class="text-sm text-foreground">
            {{ automation.trigger.type === "schedule" ? "Schedule" : "Event" }}:
            <code class="font-mono text-sm">{{ automation.trigger.value }}</code>
          </p>
        </div>

        <!-- Policy -->
        <div class="space-y-1">
          <p class="text-xs font-semibold uppercase tracking-[0.12em] text-muted-foreground">
            Policy
          </p>
          <p class="text-sm text-foreground">
            Max {{ automation.policy.maxConcurrent }} concurrent,
            {{ automation.policy.maxPerHour }}/hour,
            {{ automation.policy.timeoutMinutes }}min timeout
          </p>
        </div>
      </div>

      <!-- Action icons -->
      <div class="flex items-center gap-2">
        <button
          v-if="!automation.enabled"
          class="p-2 text-muted-foreground transition-colors hover:text-foreground"
          title="Run automation"
          @click="$emit('play', automation.id)"
        >
          <Play :size="18" />
        </button>
        <button
          v-if="automation.enabled"
          class="p-2 text-muted-foreground transition-colors hover:text-foreground"
          title="Pause automation"
          @click="$emit('pause', automation.id)"
        >
          <Pause :size="18" />
        </button>
        <button
          class="p-2 text-muted-foreground transition-colors hover:text-foreground"
          title="Edit automation"
          @click="$emit('edit', automation.id)"
        >
          <Edit :size="18" />
        </button>
        <button
          class="p-2 text-muted-foreground transition-colors hover:text-destructive"
          title="Delete automation"
          @click="$emit('delete', automation.id)"
        >
          <Trash2 :size="18" />
        </button>
      </div>
    </div>
  </article>
</template>
