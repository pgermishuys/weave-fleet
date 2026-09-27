<script setup lang="ts">
import { storeToRefs } from "pinia";
import { useNoticesStore, type Notice } from "@/stores/notices";

const store = useNoticesStore();
const { chips } = storeToRefs(store);

function onClick(notice: Notice): void {
  if (notice.onChipClick) notice.onChipClick();
  else store.reopen(notice.id);
}
</script>

<template>
  <div
    v-if="chips.length"
    class="notice-chips"
  >
    <button
      v-for="notice in chips"
      :key="notice.id"
      type="button"
      class="notice-chip"
      :class="{ 'notice-chip--quiet': notice.quiet }"
      :data-notice-chip="notice.id"
      data-testid="notice-chip"
      :title="notice.title"
      @click="onClick(notice)"
    >
      <span
        class="notice-chip__dot"
        aria-hidden="true"
      />
      {{ notice.chip }}
    </button>
  </div>
</template>

<style scoped>
.notice-chips {
  display: flex;
  flex-shrink: 0;
  align-items: center;
  gap: 6px;
}

/* Where a notice settles: small, and it stays until whatever posted it is done. */
.notice-chip {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  height: 18px;
  padding: 0 8px 0 7px;
  border: 0;
  border-radius: 999px;
  background: var(--accent-dim);
  color: var(--accent);
  font: inherit;
  font-size: 11px;
  font-weight: 500;
  font-variant-numeric: tabular-nums;
  white-space: nowrap;
  cursor: pointer;
  animation: notice-chip-in 220ms 180ms ease-out both;
  transition: background var(--transition);
}

.notice-chip:hover {
  background: color-mix(in srgb, var(--accent) 24%, transparent);
}

.notice-chip:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 1px;
}

.notice-chip__dot {
  position: relative;
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: var(--accent);
}

/* Two rings as it arrives, then still. */
.notice-chip__dot::after {
  content: "";
  position: absolute;
  inset: -4px;
  border: 1.5px solid var(--accent);
  border-radius: 50%;
  opacity: 0;
  animation: notice-chip-ring 1.1s 400ms ease-out 2;
}

/* A confirmation that goes away by itself ("Updated to …"). */
.notice-chip--quiet {
  background: transparent;
  color: var(--muted);
}

.notice-chip--quiet:hover {
  background: color-mix(in srgb, var(--text) 6%, transparent);
  color: var(--text);
}

.notice-chip--quiet .notice-chip__dot {
  background: var(--running);
}

.notice-chip--quiet .notice-chip__dot::after {
  display: none;
}

@keyframes notice-chip-in {
  from {
    opacity: 0;
  }
  to {
    opacity: 1;
  }
}

@keyframes notice-chip-ring {
  0% {
    opacity: 0.8;
    transform: scale(0.5);
  }
  100% {
    opacity: 0;
    transform: scale(1.6);
  }
}

@media (prefers-reduced-motion: reduce) {
  .notice-chip,
  .notice-chip__dot::after {
    animation: none;
  }
}
</style>
