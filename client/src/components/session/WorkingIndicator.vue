<script setup lang="ts">
import { computed, onBeforeUnmount, shallowRef, watch } from "vue";
import StatusGlyph from "@/components/sessions/StatusGlyph.vue";
import { elapsedTickMs, formatElapsed } from "@/lib/running-work";
import { describeRetry, type RetryStatus } from "@/lib/retry-status";

const props = defineProps<{
  /** When the turn began (your last prompt), in epoch milliseconds; the time is left out without it. */
  since?: number | null;
  /** The turn is stopped on a question (a sub-agent's or its own) and waits for you, not for the agent. */
  waiting?: boolean;
  /** The harness is waiting to retry a failed model call: why, and when it tries again. */
  retry?: RetryStatus | null;
}>();

// Ticks every second in the turn's first minute, then once a minute, as the strip's times do.
const now = shallowRef(Date.now());
let timer: ReturnType<typeof setTimeout> | undefined;
function tick(): void {
  now.value = Date.now();
  // A retry counts down to its next attempt in seconds.
  timer = setTimeout(tick, props.retry || !props.since ? 1_000 : elapsedTickMs(now.value - props.since));
}
tick();
// A new turn starts counting seconds again at once.
watch(() => [props.since, props.retry], () => {
  clearTimeout(timer);
  tick();
});
onBeforeUnmount(() => clearTimeout(timer));

const elapsed = computed(() => (props.since ? formatElapsed(now.value - props.since) : null));
const retrying = computed(() => (props.retry ? describeRetry(props.retry, now.value) : null));
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
  <!-- Still in the turn, waiting out a failed model call: the row's amber dots, and why. -->
  <div
    v-else-if="retrying"
    class="working working--retry"
    role="status"
    data-testid="working-indicator"
  >
    <StatusGlyph
      status="active"
      activity="retry"
      label="Retrying"
    />
    <span
      class="working__retry"
      data-testid="working-retry"
    >{{ retrying }}</span>
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
/* Why and when can be long: it wraps rather than cutting off the reason. */
.working--retry {
  height: auto;
  min-height: 24px;
}

.working__retry {
  min-width: 0;
  color: var(--status-waiting);
  line-height: 1.45;
}

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
