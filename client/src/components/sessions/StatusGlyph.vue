<script setup lang="ts">
interface Props {
  status: string;
}

const props = defineProps<Props>();

const COLOR_MAP: Record<string, string> = {
  completed: "var(--complete)",
  idle: "var(--idle)",
  resuming: "var(--running)",
  stopped: "var(--muted)",
  disconnected: "var(--muted)",
  error: "var(--error)",
  waiting_input: "var(--queued)",
};

function statusColor(status: string): string {
  return COLOR_MAP[status] ?? "var(--running)";
}

function statusLabel(status: string): string {
  switch (status) {
    case "active": return "Active";
    case "idle": return "Idle";
    case "resuming": return "Resuming";
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
  <!-- active: filled circle -->
  <svg
    v-if="props.status === 'active'"
    width="8"
    height="8"
    viewBox="0 0 8 8"
    fill="none"
    aria-hidden="false"
    :aria-label="statusLabel(props.status)"
    class="status-glyph"
  >
    <circle cx="4" cy="4" r="4" :fill="statusColor(props.status)" />
  </svg>

  <!-- resuming: pulsing filled circle -->
  <svg
    v-else-if="props.status === 'resuming'"
    width="8"
    height="8"
    viewBox="0 0 8 8"
    fill="none"
    aria-hidden="false"
    :aria-label="statusLabel(props.status)"
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
    :aria-label="statusLabel(props.status)"
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
    :aria-label="statusLabel(props.status)"
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
    :aria-label="statusLabel(props.status)"
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
    :aria-label="statusLabel(props.status)"
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
    :aria-label="statusLabel(props.status)"
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

@keyframes glyph-pulse {
  0%, 100% { opacity: 1; }
  50% { opacity: 0.3; }
}
</style>
