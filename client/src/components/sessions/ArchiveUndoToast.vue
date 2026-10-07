<script setup lang="ts">
import { computed } from "vue";
import { Undo2, X } from "lucide-vue-next";
import { ARCHIVE_UNDO_MS, useArchiveQueueStore } from "@/stores/archive-queue";
import { LINEAGE_UNDO_MS, useLineageMovesStore } from "@/stores/lineage-moves";
import { useSessionPinsStore } from "@/stores/session-pins";

// One toast for what the session list lets you undo: archiving, and moving a session out of its parent (or back).
// It also says when pinning or unpinning failed.
const archiveQueue = useArchiveQueueStore();
const lineageMoves = useLineageMovesStore();
const pins = useSessionPinsStore();

const pending = computed(() => {
  if (archiveQueue.pending) {
    const { message, ids } = archiveQueue.pending;
    return { key: `archive:${message}${ids.join()}`, message, duration: ARCHIVE_UNDO_MS, undo: () => archiveQueue.undo() };
  }
  if (lineageMoves.pending) {
    const { message, sessionId, detached } = lineageMoves.pending;
    return { key: `lineage:${sessionId}:${detached}`, message, duration: LINEAGE_UNDO_MS, undo: () => void lineageMoves.undo() };
  }
  return null;
});
const error = computed(() => archiveQueue.error ?? lineageMoves.error ?? pins.error);

function dismissError(): void {
  archiveQueue.dismissError();
  lineageMoves.dismissError();
  pins.dismissError();
}

const drainStyle = computed(() => ({ animationDuration: `${pending.value?.duration ?? ARCHIVE_UNDO_MS}ms` }));
</script>

<template>
  <Transition name="archive-toast">
    <div
      v-if="pending"
      :key="pending.key"
      class="archive-toast"
      role="status"
      data-testid="archive-undo-toast"
    >
      <div class="archive-toast__row">
        <span class="archive-toast__message">{{ pending.message }}</span>
        <button
          type="button"
          class="archive-toast__action"
          data-testid="archive-undo-button"
          @click="pending.undo()"
        >
          <Undo2 aria-hidden="true" />
          Undo
        </button>
      </div>
      <div
        class="archive-toast__drain"
        :style="drainStyle"
      />
    </div>
    <div
      v-else-if="error"
      class="archive-toast archive-toast--error"
      role="alert"
    >
      <div class="archive-toast__row">
        <span class="archive-toast__message">{{ error }}</span>
        <button
          type="button"
          class="archive-toast__action"
          aria-label="Dismiss"
          @click="dismissError"
        >
          <X aria-hidden="true" />
        </button>
      </div>
    </div>
  </Transition>
</template>

<style scoped>
/* Inverted so it reads over any panel; the bar drains while Undo is still possible. */
.archive-toast {
  position: fixed;
  left: 50%;
  bottom: max(16px, env(safe-area-inset-bottom));
  z-index: 60;
  width: min(420px, calc(100vw - 32px));
  transform: translateX(-50%);
  overflow: hidden;
  border-radius: var(--radius-card);
  background: var(--text);
  color: var(--main-bg);
  font-size: 13px;
  box-shadow: 0 12px 32px -12px rgba(0, 0, 0, 0.45);
}

.archive-toast__row {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 9px 8px 9px 14px;
}

.archive-toast__message {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.archive-toast__action {
  display: inline-flex;
  flex-shrink: 0;
  align-items: center;
  gap: 5px;
  padding: 3px 10px;
  border: 0;
  border-radius: 999px;
  background: color-mix(in srgb, var(--main-bg) 16%, transparent);
  color: inherit;
  font: inherit;
  font-weight: 600;
  cursor: pointer;
  transition: background var(--transition);
}

.archive-toast__action:hover {
  background: color-mix(in srgb, var(--main-bg) 26%, transparent);
}

.archive-toast__action:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}

.archive-toast__action svg {
  width: 13px;
  height: 13px;
}

.archive-toast--error .archive-toast__message {
  white-space: normal;
}

.archive-toast__drain {
  height: 2px;
  background: color-mix(in srgb, var(--main-bg) 45%, transparent);
  transform-origin: left;
  animation: archive-toast-drain linear forwards;
}

@keyframes archive-toast-drain {
  from { transform: scaleX(1); }
  to { transform: scaleX(0); }
}

.archive-toast-enter-active,
.archive-toast-leave-active {
  transition: opacity var(--transition), transform var(--transition);
}

.archive-toast-enter-from,
.archive-toast-leave-to {
  opacity: 0;
  transform: translate(-50%, 8px);
}

@media (prefers-reduced-motion: reduce) {
  .archive-toast__drain {
    animation: none;
  }

  .archive-toast-enter-active,
  .archive-toast-leave-active {
    transition: none;
  }
}
</style>
