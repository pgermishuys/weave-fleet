<script setup lang="ts">
import { CornerDownRight, X } from "lucide-vue-next";
import { Button } from "@/components/ui/button";
import type { QueuedMessage } from "@/composables/use-session-queue";

defineOptions({
  name: "QueuedMessages",
});

/**
 * The messages the user sent while the agent works, in the order they go: the first is sent when the turn ends. Each
 * can be taken back, or, where the session's harness can take a message mid-turn, sent now instead of waiting.
 */
defineProps<{
  items: readonly QueuedMessage[];
  /** Whether each item can be sent now, into the running turn. */
  canSendNow: (item: QueuedMessage) => boolean;
}>();

const emit = defineEmits<{
  sendNow: [index: number];
  remove: [index: number];
}>();
</script>

<template>
  <ol
    class="queued"
    aria-label="Queued messages"
    data-testid="queued-messages"
  >
    <li
      v-for="(item, index) in items"
      :key="item.id"
      class="queued__item"
      data-testid="queued-message"
    >
      <span class="queued__label">{{ index === 0 ? "Next" : "Queued" }}</span>
      <span
        class="queued__text"
        :title="item.text"
      >{{ item.text }}</span>
      <Button
        v-if="canSendNow(item)"
        variant="ghost"
        size="sm"
        class="queued__send-now"
        data-testid="queued-send-now"
        title="Send now: the agent reads it at its next step, without waiting for the turn to end"
        @click="emit('sendNow', index)"
      >
        <CornerDownRight
          class="size-3.5"
          aria-hidden="true"
        />
        Send now
      </Button>
      <Button
        variant="toolbar-icon"
        size="toolbar"
        :aria-label="`Remove queued message: ${item.text}`"
        title="Remove from the queue"
        data-testid="queued-remove"
        @click="emit('remove', index)"
      >
        <X
          class="size-3.5"
          aria-hidden="true"
        />
      </Button>
    </li>
  </ol>
</template>

<style scoped>
.queued {
  display: flex;
  flex-direction: column;
  gap: 2px;
  max-width: 760px;
  margin: 0 auto 6px;
  padding: 0;
  list-style: none;
}

.queued__item {
  display: flex;
  align-items: center;
  gap: 8px;
  min-width: 0;
  padding: 3px 4px 3px 12px;
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  background: var(--card-bg);
  font-size: 12px;
}

.queued__label {
  flex-shrink: 0;
  color: var(--muted);
  font-size: 11px;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.04em;
}

.queued__text {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  color: var(--text);
  text-overflow: ellipsis;
  white-space: nowrap;
}

.queued__send-now {
  height: 26px;
  padding: 0 8px;
  font-size: 12px;
}
</style>
