<script setup lang="ts">
import { computed, onBeforeUnmount, shallowRef, watch } from "vue";
import { Play, RotateCw, X } from "lucide-vue-next";
import { formatRetryClock, formatRetryIn, type ScheduledRetry } from "@/lib/turn-retry";

/**
 * On the failure card of a turn a model provider's limit stopped: when Fleet tries again, ticking down, with Try now
 * and Don't retry.
 */
const props = defineProps<{
  retry: ScheduledRetry;
  busy?: boolean;
  error?: string;
}>();

const emit = defineEmits<{
  sendNow: [];
  cancel: [];
}>();

const now = shallowRef(Date.now());
let timer: ReturnType<typeof setTimeout> | null = null;

// Every second in the last minute, every 15 seconds before.
function tick(): void {
  now.value = Date.now();
  const left = Date.parse(props.retry.dueAt) - now.value;
  timer = setTimeout(tick, left > 0 && left <= 60_000 ? 1000 : 15_000);
}
tick();
watch(() => props.retry.dueAt, () => {
  if (timer !== null) clearTimeout(timer);
  tick();
});
onBeforeUnmount(() => {
  if (timer !== null) clearTimeout(timer);
});

const when = computed(() => {
  const left = formatRetryIn(props.retry.dueAt, now.value);
  return left === "now" ? "Fleet is trying again now" : `Fleet tries again at ${formatRetryClock(props.retry.dueAt, now.value)} · ${left}`;
});

const why = computed(() => {
  const source = props.retry.providerSaid
    ? props.retry.kind === "usage_limit" ? "when the limit resets" : "when the provider said to"
    : "after a wait";
  return props.retry.attempt > 1 ? `Attempt ${props.retry.attempt}, ${source}.` : `It carries on ${source}. Nothing was lost.`;
});
</script>

<template>
  <div
    class="turn-retry"
    data-testid="turn-retry"
  >
    <RotateCw
      class="turn-retry__icon"
      aria-hidden="true"
    />
    <div class="turn-retry__text">
      <span
        class="turn-retry__when"
        role="status"
        data-testid="turn-retry-when"
      >{{ when }}</span>
      <span class="turn-retry__why">{{ error ?? why }}</span>
    </div>
    <span class="turn-retry__actions">
      <button
        class="turn-retry__button"
        type="button"
        data-testid="turn-retry-now"
        :disabled="busy"
        @click="emit('sendNow')"
      >
        <Play
          class="turn-retry__button-icon"
          aria-hidden="true"
        />
        Try now
      </button>
      <button
        class="turn-retry__button"
        type="button"
        data-testid="turn-retry-cancel"
        :disabled="busy"
        @click="emit('cancel')"
      >
        <X
          class="turn-retry__button-icon"
          aria-hidden="true"
        />
        Don't retry
      </button>
    </span>
  </div>
</template>

<style scoped>
.turn-retry {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
  margin-top: 8px;
  padding: 8px 10px;
  border-radius: calc(var(--radius-card) - 2px);
  background: color-mix(in srgb, var(--status-waiting) 10%, var(--panel-bg));
}

.turn-retry__icon {
  width: 14px;
  height: 14px;
  flex: none;
  color: var(--status-waiting);
}

.turn-retry__text {
  display: flex;
  /* On a narrow card the buttons go under the words rather than squeezing them. */
  flex: 1 1 220px;
  min-width: 0;
  flex-direction: column;
  gap: 1px;
}

.turn-retry__when {
  color: var(--text);
  font-size: 12.5px;
  font-weight: 500;
  font-variant-numeric: tabular-nums;
}

.turn-retry__why {
  color: var(--muted);
  font-size: 11.5px;
}

.turn-retry__actions {
  display: inline-flex;
  flex: none;
  margin-left: auto;
  flex-wrap: wrap;
  justify-content: flex-end;
  gap: 6px;
}

.turn-retry__button {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  padding: 4px 10px;
  border: 1px solid var(--border);
  border-radius: 999px;
  background: var(--panel-bg);
  color: var(--text);
  font-size: 12px;
  cursor: pointer;
  transition: var(--transition);
}

.turn-retry__button:hover:not(:disabled) {
  border-color: var(--accent);
  color: var(--accent);
}

.turn-retry__button:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.turn-retry__button-icon {
  width: 12px;
  height: 12px;
}
</style>
