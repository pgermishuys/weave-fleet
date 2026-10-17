<script setup lang="ts">
import InterruptedCard from "@/components/session/InterruptedCard.vue";
import { useSessionInterruption } from "@/composables/use-session-interruption";

/** Loads a session's interruption, keeps it current, and shows the card. Nothing when it has none. */
const props = defineProps<{ sessionId: string }>();

const { interruption, busy, error, continueSession, dismiss } = useSessionInterruption(props.sessionId);
</script>

<template>
  <InterruptedCard
    v-if="interruption"
    :interruption="interruption"
    :busy="busy"
    :error="error"
    @continue="continueSession"
    @dismiss="dismiss"
  />
</template>
