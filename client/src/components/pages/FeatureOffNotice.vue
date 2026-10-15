<script setup lang="ts">
import { ref } from "vue";
import { Button } from "@/components/ui/button";

const props = defineProps<{
  title: string;
  /** Says what the feature is for, so a newcomer knows what they'd be turning on. */
  description: string;
  turnOn: () => Promise<void>;
}>();

const turningOn = ref(false);

async function onTurnOn(): Promise<void> {
  turningOn.value = true;
  try {
    await props.turnOn();
  } finally {
    turningOn.value = false;
  }
}
</script>

<template>
  <section
    class="mx-auto flex h-full max-w-2xl flex-col items-center justify-center p-8 text-center"
    data-testid="feature-off"
  >
    <h1 class="text-2xl font-semibold text-text">
      {{ title }} is off
    </h1>
    <p class="mt-2 text-sm text-muted">
      {{ description }}
    </p>
    <Button
      class="mt-4"
      :disabled="turningOn"
      data-testid="feature-turn-on"
      @click="onTurnOn"
    >
      Turn on
    </Button>
    <p class="mt-2 text-xs text-muted">
      You can change this any time in Settings.
    </p>
  </section>
</template>
