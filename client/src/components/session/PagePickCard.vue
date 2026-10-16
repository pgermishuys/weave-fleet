<script setup lang="ts">
import { PanelsTopLeft } from "lucide-vue-next";
import { useWaitingPage } from "@/composables/use-waiting-page";

/**
 * Shown while the agent waits for a pick on a page it showed: the session is stopped on you, and the page is in the
 * side panel. Typing a message instead answers too, so the card says so.
 */
const props = defineProps<{ sessionId: string }>();
const emit = defineEmits<{ open: [canvasId: string] }>();

const waitingPage = useWaitingPage(() => props.sessionId);
</script>

<template>
  <div
    v-if="waitingPage"
    class="page-pick"
    data-testid="page-pick-card"
  >
    <PanelsTopLeft
      :size="16"
      class="page-pick__icon"
      aria-hidden="true"
    />
    <div class="page-pick__body">
      <p class="page-pick__title">
        Pick on the page
      </p>
      <p class="page-pick__line">
        {{ waitingPage.page?.title }} · or type a message to answer instead
      </p>
    </div>
    <button
      type="button"
      class="page-pick__open"
      data-testid="page-pick-open"
      @click="emit('open', waitingPage.server?.canvasId ?? waitingPage.id)"
    >
      Open page
    </button>
  </div>
</template>

<style scoped>
.page-pick {
  display: flex;
  align-items: center;
  gap: 10px;
  margin-top: 10px;
  padding: 12px;
  border: 1px solid color-mix(in srgb, var(--status-waiting) 40%, var(--border));
  border-radius: var(--radius-card);
  background: color-mix(in srgb, var(--status-waiting) 5%, var(--card-bg));
}

.page-pick__icon {
  flex-shrink: 0;
  color: var(--status-waiting);
}

.page-pick__body {
  flex: 1;
  min-width: 0;
}

.page-pick__title {
  margin: 0;
  font-size: 13px;
  font-weight: 600;
  color: var(--text);
}

.page-pick__line {
  margin: 2px 0 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 12px;
  color: var(--muted);
}

.page-pick__open {
  flex-shrink: 0;
  padding: 4px 10px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  font-size: 12px;
  color: var(--text);
}

.page-pick__open:hover {
  background: var(--accent-dim);
}
</style>
