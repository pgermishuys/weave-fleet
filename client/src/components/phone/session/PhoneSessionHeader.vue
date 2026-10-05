<script setup lang="ts">
import { ChevronLeft, MoreHorizontal } from "lucide-vue-next";
import { Button } from "@/components/ui/button";

/**
 * The header is the status: back, the session's title, and under it the machine, its state and how long — "Working ·
 * 1m 12s", "Needs you · 2m" (amber), "Finished · 14:44" — or when the machine was last heard from.
 */
defineProps<{
  title: string;
  machineName: string;
  tone: "working" | "needs-you" | "finished" | "error" | "unreachable" | "idle";
  state: string;
  detail: string;
}>();
const emit = defineEmits<{ (event: "back"): void; (event: "menu"): void }>();
</script>

<template>
  <header
    class="psh"
    data-testid="phone-session-header"
  >
    <Button
      variant="toolbar-icon"
      size="icon"
      aria-label="Back"
      data-testid="phone-session-back"
      @click="emit('back')"
    >
      <ChevronLeft :size="22" />
    </Button>
    <div class="psh__body">
      <h1 class="psh__title">
        {{ title }}
      </h1>
      <p class="psh__line">
        <span class="psh__machine">{{ machineName }}</span>
        <span
          class="psh__state"
          :class="`psh__state--${tone}`"
          data-testid="phone-session-state"
        ><span
          class="psh__dot"
          aria-hidden="true"
        />{{ state }}</span>
        <span
          v-if="detail"
          class="psh__detail"
        >· {{ detail }}</span>
      </p>
    </div>
    <Button
      variant="toolbar-icon"
      size="icon"
      aria-label="More"
      data-testid="phone-session-menu"
      @click="emit('menu')"
    >
      <MoreHorizontal :size="20" />
    </Button>
  </header>
</template>

<style scoped>
.psh {
  display: flex;
  flex: none;
  align-items: center;
  gap: 4px;
  min-height: 56px;
  padding: 4px 4px 6px;
  border-bottom: 1px solid var(--border);
  background: var(--main-bg);
}

.psh__body {
  display: grid;
  flex: 1;
  min-width: 0;
}

.psh__title {
  overflow: hidden;
  font-size: 15px;
  font-weight: 600;
  white-space: nowrap;
  text-overflow: ellipsis;
}

.psh__line {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
  font-size: 12px;
  color: var(--muted);
}

.psh__machine {
  padding: 0 5px;
  border: 1px solid var(--border);
  border-radius: 6px;
  background: var(--card-bg);
  font-size: 11px;
  color: var(--text);
}

.psh__state {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  font-weight: 500;
}

.psh__dot {
  width: 7px;
  height: 7px;
  border-radius: 50%;
  background: currentColor;
}

.psh__state--working {
  color: var(--running);
}

.psh__state--needs-you {
  color: var(--idle);
}

.psh__state--finished {
  color: var(--complete);
}

.psh__state--error,
.psh__state--unreachable {
  color: var(--error);
}

.psh__state--idle {
  color: var(--muted);
}

.psh__detail {
  white-space: nowrap;
}
</style>
