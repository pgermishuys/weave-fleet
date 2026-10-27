<script setup lang="ts">
import type { Tile } from "@/lib/mission-control";

defineProps<{ tile: Tile; selected: boolean }>();

const DOT: Record<Tile["state"], string> = {
  "needs-you": "bg-accent",
  working: "bg-running",
  idle: "bg-idle",
  error: "bg-error",
};
</script>

<template>
  <button
    type="button"
    class="w-full rounded-card border bg-main-bg p-3 text-left transition-colors hover:border-accent/50"
    :class="[selected ? 'border-accent' : 'border-border', tile.stale ? 'opacity-60' : '']"
    data-testid="mission-tile"
    :data-state="tile.state"
  >
    <p class="flex items-center gap-2 text-sm font-medium text-text">
      <span
        class="h-2 w-2 shrink-0 rounded-full"
        :class="DOT[tile.state]"
        aria-hidden="true"
      />
      <span class="truncate">{{ tile.title }}</span>
    </p>
    <p class="mt-1 flex flex-wrap items-center gap-1.5 text-xs text-muted">
      <span
        class="rounded border border-border px-1.5 py-px font-mono text-text"
        data-testid="tile-machine"
      >{{ tile.machineName }}</span>
      <span>{{ tile.harness }}<template v-if="tile.model"> · {{ tile.model }}</template></span>
    </p>
    <p
      class="mt-2 truncate rounded bg-panel-bg px-2 py-1 font-mono text-xs text-text"
      data-testid="tile-now"
    >
      {{ tile.now }}
    </p>
    <p
      v-if="tile.todos || tile.helpers"
      class="mt-2 flex items-center gap-3 text-xs text-muted"
    >
      <span v-if="tile.todos">{{ tile.todos.done }}/{{ tile.todos.total }}</span>
      <span v-if="tile.helpers">{{ tile.helpers }} {{ tile.helpers === 1 ? "helper" : "helpers" }}</span>
    </p>
  </button>
</template>
