<script setup lang="ts">
import { computed, onBeforeUnmount, shallowRef } from "vue";
import { useSessionRetry } from "@/composables/use-session-retry";
import { formatRetryClock, formatRetryIn } from "@/lib/turn-retry";

/** Under a turn a model provider's limit stopped: when Fleet tries again, with Try now and Don't retry. */
const props = defineProps<{ sessionId: string }>();

const { retry, busy, sendNow, cancel } = useSessionRetry(props.sessionId);

const now = shallowRef(Date.now());
const timer = setInterval(() => {
  now.value = Date.now();
}, 15_000);
onBeforeUnmount(() => clearInterval(timer));

const when = computed(() => {
  if (!retry.value) return "";
  const left = formatRetryIn(retry.value.dueAt, now.value);
  return left === "now" ? "Fleet is trying again now" : `Fleet tries again at ${formatRetryClock(retry.value.dueAt, now.value)} · ${left}`;
});
</script>

<template>
  <div
    v-if="retry"
    class="ph-banner ps-retry"
    data-testid="phone-retry"
  >
    <p
      class="ps-retry__when"
      role="status"
    >
      {{ when }}
    </p>
    <p class="ps-retry__actions">
      <button
        type="button"
        class="ph-btn ph-btn--outline ph-btn--sm"
        :disabled="busy"
        @click="sendNow"
      >
        Try now
      </button>
      <button
        type="button"
        class="ph-btn ph-btn--outline ph-btn--sm"
        :disabled="busy"
        @click="cancel"
      >
        Don't retry
      </button>
    </p>
  </div>
</template>

<style scoped>
.ps-retry {
  display: flex;
  flex-direction: column;
  gap: 8px;
  width: auto;
  margin: 0;
}

.ps-retry__when {
  margin: 0;
  font-variant-numeric: tabular-nums;
}

.ps-retry__actions {
  display: flex;
  gap: 8px;
  margin: 0;
}
</style>
