<script setup lang="ts">
import BackgroundStrip from "@/components/session/BackgroundStrip.vue";

defineProps<{
  sessionId: string;
  /** The parent session's title, or null when it isn't known yet. */
  parentTitle: string | null;
  /** Why the session takes no prompt, from its capabilities; shown as the tooltip. */
  reason: string | null;
}>();

const emit = defineEmits<{
  back: [];
}>();
</script>

<template>
  <!-- In place of the composer for a subagent's session its harness can't prompt (Claude Code): its work belongs to
       the parent's turn, so a message here would wait for good. -->
  <div class="subagent-prompt-line">
    <BackgroundStrip :session-id="sessionId" />
    <p
      class="subagent-prompt-line__body"
      role="note"
      data-testid="subagent-prompt-line"
      :title="reason ?? undefined"
    >
      <template v-if="parentTitle">
        This is a subagent of
        <a
          href="#"
          class="subagent-prompt-line__parent"
          @click.prevent="emit('back')"
        >{{ parentTitle }}</a>. Ask there.
      </template>
      <template v-else>
        This is a subagent's session. Ask the session that started it.
      </template>
    </p>
  </div>
</template>

<style scoped>
/* On the composer's column, as quiet as the recap line. */
.subagent-prompt-line {
  flex-shrink: 0;
  padding: 4px 24px 18px;
}

.subagent-prompt-line__body {
  max-width: 760px;
  margin: 0 auto;
  padding: 10px 4px;
  color: var(--muted);
  font-size: 13px;
  line-height: 1.5;
  text-align: center;
}

.subagent-prompt-line__parent {
  color: var(--text);
  font-weight: 500;
  text-decoration: none;
}

.subagent-prompt-line__parent:hover {
  text-decoration: underline;
}
</style>
