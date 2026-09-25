<script setup lang="ts">
import { computed } from "vue";
import { useRouter } from "@tanstack/vue-router";
import { MessageCircleQuestionMark, SquareArrowOutUpRight, X } from "lucide-vue-next";
import ActivityStream from "@/components/session/ActivityStream.vue";
import { Button } from "@/components/ui/button";
import { useSideConversation } from "@/composables/use-side-conversation";

/**
 * A session's side conversation (`/btw`), docked above the composer. It's a fork of the session made at its last
 * finished turn, so only what was asked in it shows here; the session itself carries on undisturbed. While it's open
 * the composer talks to it. Close deletes the fork; Keep as session makes it a session of its own.
 */
const props = defineProps<{
  sessionId: string;
}>();

const router = useRouter();
const { side, isOpen, starting, close, keep } = useSideConversation(() => props.sessionId);

const title = computed(() => side.value?.title.replace(/^btw:\s*/, "") ?? starting.value ?? "");

async function handleKeep(): Promise<void> {
  const kept = await keep();
  if (kept) {
    await router.navigate({
      to: "/sessions/$id",
      params: { id: kept.sessionId },
      search: { instanceId: kept.instanceId, parentSessionId: undefined },
    });
  }
}
</script>

<template>
  <section
    v-if="isOpen"
    class="side-conversation"
    aria-label="Side conversation"
    data-testid="side-conversation"
  >
    <div class="side-conversation__card">
      <header class="side-conversation__head">
        <MessageCircleQuestionMark
          class="side-conversation__icon"
          aria-hidden="true"
        />
        <span class="side-conversation__label">btw</span>
        <span
          class="side-conversation__title"
          :title="title"
        >{{ title }}</span>
        <span class="side-conversation__hint">A fork at the last finished turn · the session carries on</span>
        <Button
          v-if="side"
          variant="toolbar-icon"
          size="sm"
          class="side-conversation__keep"
          data-testid="side-conversation-keep"
          title="Keep as a session of its own"
          @click="handleKeep"
        >
          <SquareArrowOutUpRight
            class="side-conversation__action-icon"
            aria-hidden="true"
          />
          <span>Keep as session</span>
        </Button>
        <Button
          variant="toolbar-icon"
          size="toolbar"
          data-testid="side-conversation-close"
          aria-label="Close the side conversation"
          title="Close (the side conversation is deleted)"
          :disabled="!side"
          @click="close"
        >
          <X
            class="side-conversation__action-icon"
            aria-hidden="true"
          />
        </Button>
      </header>
      <div class="side-conversation__body">
        <ActivityStream
          v-if="side"
          :key="side.sessionId"
          :session-id="side.sessionId"
          :after="side.boundaryMessageId"
        />
        <p
          v-else
          class="side-conversation__starting"
          role="status"
        >
          Forking the session at its last finished turn…
        </p>
      </div>
    </div>
  </section>
</template>

<style scoped>
/* On the composer's column, right above it: the composer is its input while it's open. */
.side-conversation {
  flex-shrink: 0;
  padding: 6px 24px 0;
}

.side-conversation__card {
  display: flex;
  flex-direction: column;
  max-width: 760px;
  height: min(42vh, 400px);
  margin: 0 auto;
  overflow: hidden;
  border: 1px solid color-mix(in srgb, var(--accent) 45%, var(--border));
  border-radius: calc(var(--radius-panel) + 2px);
  background: var(--card-bg);
}

.side-conversation__head {
  display: flex;
  align-items: center;
  gap: 8px;
  min-width: 0;
  padding: 8px 8px 8px 14px;
  border-bottom: 1px solid var(--border);
  font-size: 12px;
}

.side-conversation__icon {
  width: 14px;
  height: 14px;
  flex-shrink: 0;
  color: var(--accent);
}

.side-conversation__label {
  flex-shrink: 0;
  color: var(--accent);
  font-weight: 600;
}

.side-conversation__title {
  min-width: 0;
  overflow: hidden;
  color: var(--text);
  font-weight: 500;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.side-conversation__hint {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  color: var(--muted);
  text-overflow: ellipsis;
  white-space: nowrap;
}

.side-conversation__keep {
  height: 26px;
  flex-shrink: 0;
  font-size: 12px;
}

.side-conversation__action-icon {
  width: 14px;
  height: 14px;
}

.side-conversation__body {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-height: 0;
}

/* The conversation's own stream, set closer: the card is its frame. */
.side-conversation__body :deep(.activity-stream) {
  padding: 12px 16px 8px;
}

.side-conversation__starting {
  margin: auto;
  color: var(--muted);
  font-size: 13px;
}

@media (max-width: 640px) {
  .side-conversation {
    padding: 6px 12px 0;
  }

  .side-conversation__hint,
  .side-conversation__keep span {
    display: none;
  }
}
</style>
