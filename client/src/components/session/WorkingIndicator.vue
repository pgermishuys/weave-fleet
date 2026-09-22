<script setup lang="ts">
import { computed, onBeforeUnmount, shallowRef } from "vue";
import StatusGlyph from "@/components/sessions/StatusGlyph.vue";

const props = defineProps<{
  /** When the turn began (your last prompt), in epoch milliseconds; the time is left out without it. */
  since?: number | null;
  /** The turn is stopped on a question (a sub-agent's or its own) and waits for you, not for the agent. */
  waiting?: boolean;
}>();

const now = shallowRef(Date.now());
const timer = setInterval(() => {
  now.value = Date.now();
}, 1000);
onBeforeUnmount(() => clearInterval(timer));

const elapsed = computed(() => {
  if (!props.since) return null;
  const seconds = Math.max(0, Math.floor((now.value - props.since) / 1000));
  if (seconds < 60) return `${seconds}s`;
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes}m ${seconds % 60}s`;
  return `${Math.floor(minutes / 60)}h ${minutes % 60}m`;
});
</script>

<template>
  <!-- A question holds the turn: the words and diamond the session row and header use for it. -->
  <div
    v-if="waiting"
    class="working working--waiting"
    role="status"
    data-testid="working-indicator"
  >
    <StatusGlyph
      status="waiting_input"
      label="Needs input"
    />
    <span class="working__waiting">Needs input</span>
  </div>
  <!-- The session row's Quad, so the conversation and the list say "working" the same way. -->
  <div
    v-else
    class="working"
    role="status"
    data-testid="working-indicator"
  >
    <StatusGlyph
      status="active"
      label="Working"
    />
    <span class="working__word">Working</span>
    <span
      v-if="elapsed"
      class="working__elapsed"
    >· {{ elapsed }}</span>
  </div>
</template>

<style scoped>
.working {
  display: flex;
  align-items: center;
  gap: 10px;
  height: 24px;
  color: var(--muted);
  font-size: 13px;
}

/* A highlight sweeps across the word; the glyph ticks beside it. */
.working__word {
  background: linear-gradient(
    90deg,
    var(--muted) 0%,
    var(--muted) 40%,
    var(--text) 50%,
    var(--muted) 60%,
    var(--muted) 100%
  );
  background-size: 250% 100%;
  background-clip: text;
  -webkit-background-clip: text;
  color: transparent;
  animation: working-shimmer 2.2s linear infinite;
}

.working__waiting {
  color: var(--status-waiting);
  font-weight: 500;
}

.working__elapsed {
  margin-left: -5px;
  font-variant-numeric: tabular-nums;
}

@keyframes working-shimmer {
  from { background-position: 100% 0; }
  to { background-position: -150% 0; }
}

@media (prefers-reduced-motion: reduce) {
  .working__word {
    animation: none;
    background: none;
    color: var(--muted);
  }
}
</style>
