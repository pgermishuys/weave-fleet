<script setup lang="ts">
import { computed } from "vue";

interface Props {
  status: string;
  /** Activity of an active session; "retry" turns the working dots amber. */
  activity?: string | null;
  /** Overrides the spoken label, e.g. "Delegating" or "Retrying (attempt 2)". */
  label?: string;
}

const props = defineProps<Props>();

const ariaLabel = computed(() => props.label ?? statusLabel(props.status));

// Each dot's clockwise position from the top-left, in the grid's row-major order.
const QUAD_DOTS = [0, 1, 3, 2] as const;

const COLOR_MAP: Record<string, string> = {
  completed: "var(--complete)",
  idle: "var(--status-idle)",
  running: "var(--running)",
  stopped: "var(--muted)",
  disconnected: "var(--muted)",
  error: "var(--error)",
  waiting_input: "var(--status-waiting)",
};

function statusColor(status: string): string {
  return COLOR_MAP[status] ?? "var(--running)";
}

function statusLabel(status: string): string {
  switch (status) {
    case "active": return "Working";
    case "idle": return "Idle";
    case "running": return "Running";
    case "completed": return "Completed";
    case "error": return "Error";
    case "waiting_input": return "Waiting for input";
    case "stopped": return "Stopped";
    case "disconnected": return "Disconnected";
    default: return status;
  }
}
</script>

<template>
  <!-- active: four dots in a square; the missing one travels clockwise -->
  <span
    v-if="props.status === 'active'"
    role="img"
    :aria-label="ariaLabel"
    :title="ariaLabel"
    class="status-glyph status-glyph--working"
    :class="{ 'status-glyph--retry': props.activity === 'retry' }"
  >
    <i
      v-for="slot in QUAD_DOTS"
      :key="slot"
      :style="{ animationDelay: `${(slot - 4) * 200}ms` }"
    />
  </span>

  <!-- running (a tool call in progress): pulsing filled circle -->
  <svg
    v-else-if="props.status === 'running'"
    width="8"
    height="8"
    viewBox="0 0 8 8"
    fill="none"
    aria-hidden="false"
    :aria-label="ariaLabel"
    class="status-glyph status-glyph--pulsing"
  >
    <circle cx="4" cy="4" r="4" :fill="statusColor(props.status)" />
  </svg>

  <!-- idle: hollow ring -->
  <svg
    v-else-if="props.status === 'idle'"
    width="8"
    height="8"
    viewBox="0 0 8 8"
    fill="none"
    aria-hidden="false"
    :aria-label="ariaLabel"
    class="status-glyph"
  >
    <circle cx="4" cy="4" r="3" :stroke="statusColor(props.status)" stroke-width="1.5" />
  </svg>

  <!-- error: filled triangle -->
  <svg
    v-else-if="props.status === 'error'"
    width="8"
    height="8"
    viewBox="0 0 8 8"
    fill="none"
    aria-hidden="false"
    :aria-label="ariaLabel"
    class="status-glyph"
  >
    <polygon points="4,0.5 7.5,7.5 0.5,7.5" :fill="statusColor(props.status)" />
  </svg>

  <!-- waiting_input: filled diamond -->
  <svg
    v-else-if="props.status === 'waiting_input'"
    width="8"
    height="8"
    viewBox="0 0 8 8"
    fill="none"
    aria-hidden="false"
    :aria-label="ariaLabel"
    class="status-glyph"
  >
    <polygon points="4,0.5 7.5,4 4,7.5 0.5,4" :fill="statusColor(props.status)" />
  </svg>

  <!-- completed: checkmark -->
  <svg
    v-else-if="props.status === 'completed'"
    width="8"
    height="8"
    viewBox="0 0 8 8"
    fill="none"
    aria-hidden="false"
    :aria-label="ariaLabel"
    class="status-glyph"
  >
    <polyline points="1,4.5 3.2,6.5 7,1.5" :stroke="statusColor(props.status)" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" />
  </svg>

  <!-- stopped / disconnected: filled square -->
  <svg
    v-else
    width="8"
    height="8"
    viewBox="0 0 8 8"
    fill="none"
    aria-hidden="false"
    :aria-label="ariaLabel"
    class="status-glyph"
  >
    <rect x="1" y="1" width="6" height="6" :fill="statusColor(props.status)" />
  </svg>
</template>

<style scoped>
.status-glyph {
  flex-shrink: 0;
  display: block;
}

.status-glyph--pulsing {
  animation: glyph-pulse 1.2s ease-in-out infinite;
}

/* Working: a 2×2 square of dots, 8px like the other glyphs. The missing dot
   ticks clockwise, one step every 200ms. Stepped timing means four repaints
   per cycle instead of one per frame. Text colour, because colour is kept for
   states that need the user. */
.status-glyph--working {
  display: grid;
  grid-template-columns: repeat(2, 3px);
  grid-auto-rows: 3px;
  gap: 2px;
  color: var(--text);
}

.status-glyph--working i {
  display: block;
  border-radius: 50%;
  background: currentColor;
  animation: status-quad 800ms steps(1, end) infinite;
}

.status-glyph--retry {
  color: var(--status-waiting);
}

@keyframes status-quad {
  0% { opacity: 0.16; }
  25%, 100% { opacity: 1; }
}

@media (prefers-reduced-motion: reduce) {
  .status-glyph--working i,
  .status-glyph--pulsing {
    animation: none;
  }

  /* Hold one frame: the top-left dot missing. */
  .status-glyph--working i:first-child {
    opacity: 0.16;
  }
}

@keyframes glyph-pulse {
  0%, 100% { opacity: 1; }
  50% { opacity: 0.3; }
}
</style>
