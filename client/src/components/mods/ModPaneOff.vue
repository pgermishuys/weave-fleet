<script setup lang="ts">
import { ref } from "vue";
import { Button } from "@/components/ui/button";
import { turnModOn } from "@/lib/mods/mod-off";

/** What a pane says while its mod is turned off: the same switch as Settings → Mods, then the mod draws it again. */
const props = defineProps<{ sessionId: string; mod: string }>();

const busy = ref(false);
const failed = ref(false);

async function turnOn(): Promise<void> {
  busy.value = true;
  failed.value = false;
  try {
    await turnModOn(props.sessionId, props.mod);
  } catch {
    failed.value = true;
  } finally {
    busy.value = false;
  }
}
</script>

<template>
  <div
    class="mod-pane-off"
    data-testid="mod-pane-off"
  >
    <h3 class="mod-pane-off__title">
      {{ mod }} is off
    </h3>
    <p class="mod-pane-off__text">
      This pane is drawn by the {{ mod }} mod. Turn it on to see it here.
    </p>
    <Button
      size="sm"
      :disabled="busy"
      data-testid="mod-pane-turn-on"
      @click="turnOn"
    >
      Turn on
    </Button>
    <p
      v-if="failed"
      class="mod-pane-off__text"
      role="alert"
    >
      Couldn't turn {{ mod }} on. Try again, or use Settings → Mods.
    </p>
  </div>
</template>

<style scoped>
.mod-pane-off {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 8px;
  padding: 56px 24px 24px;
  text-align: center;
}

.mod-pane-off__title {
  margin: 0;
  font-size: 15px;
  font-weight: 600;
}

.mod-pane-off__text {
  max-width: 280px;
  margin: 0 0 6px;
  font-size: 13px;
  color: var(--muted);
}
</style>
