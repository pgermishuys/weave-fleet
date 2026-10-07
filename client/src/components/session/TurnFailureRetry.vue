<script setup lang="ts">
import TurnRetryBar from "@/components/session/TurnRetryBar.vue";
import { useSessionRetry } from "@/composables/use-session-retry";

/**
 * On the latest failure card of a turn a model provider's limit stopped: loads when Fleet tries again, keeps it
 * current, and shows it with Try now and Don't retry. Nothing when no retry waits (the setting is off, or the user
 * took it from there).
 */
const props = defineProps<{ sessionId: string }>();

const { retry, busy, error, sendNow, cancel } = useSessionRetry(props.sessionId);
</script>

<template>
  <TurnRetryBar
    v-if="retry"
    :retry="retry"
    :busy="busy"
    :error="error"
    @send-now="sendNow"
    @cancel="cancel"
  />
</template>
