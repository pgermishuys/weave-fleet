<script setup lang="ts">
import { History } from "lucide-vue-next";
import type { SessionRecapPayload } from "@/lib/domain-events";

defineProps<{
  recap: SessionRecapPayload | null;
}>();
</script>

<template>
  <!-- Fleet's own voice, like Claude Code's "※ recap:" line: not the agent's, not yours. -->
  <div
    v-if="recap?.text"
    class="recap-line"
    role="note"
    aria-label="Recap"
    data-testid="recap-line"
  >
    <p class="recap-line__body">
      <History
        class="recap-line__icon"
        aria-hidden="true"
      />
      <span class="recap-line__label">recap:</span>
      {{ recap.text }}
    </p>
  </div>
</template>

<style scoped>
/* Sits between the conversation and the composer, on the composer's column. */
.recap-line {
  flex-shrink: 0;
  padding: 6px 24px 6px;
}

.recap-line__body {
  max-width: 760px;
  margin: 0 auto;
  padding: 0 4px;
  color: var(--muted);
  font-size: 13px;
  line-height: 1.5;
}

.recap-line__icon {
  display: inline-block;
  width: 13px;
  height: 13px;
  margin-right: 6px;
  vertical-align: -2px;
  color: var(--accent);
}

.recap-line__label {
  color: var(--text);
  font-weight: 500;
}
</style>
