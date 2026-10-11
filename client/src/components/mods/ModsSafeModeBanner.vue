<script setup lang="ts">
import { computed, onMounted, shallowRef } from "vue";
import { PowerOff } from "lucide-vue-next";
import { Button } from "@/components/ui/button";
import { useModsStore } from "@/stores/mods";

/**
 * Says mods are stopped while safe mode is on ("Start without mods", `?mods=off`), and turns them back on.
 * Nothing at all otherwise, so it takes no room from the panels below.
 */
const props = defineProps<{
  /** On the phone the button wears the phone's own classes: phone.css resets bare buttons, beating utilities. */
  phone?: boolean;
}>();

const mods = useModsStore();
const shown = computed(() => mods.isSwitchedOn && mods.safeMode);
const pending = shallowRef(false);
const error = shallowRef<string | null>(null);

onMounted(() => {
  if (mods.modsSwitch === null) void mods.loadSwitch();
});

async function turnBackOn(): Promise<void> {
  pending.value = true;
  error.value = null;
  try {
    await mods.setSafeMode(false);
  } catch (caught) {
    error.value = caught instanceof Error ? caught.message : "Couldn't turn mods back on.";
  } finally {
    pending.value = false;
  }
}
</script>

<template>
  <div
    v-if="shown"
    class="flex shrink-0 gap-2.5 border-b border-border bg-panel-bg text-[0.8125rem] text-foreground"
    :class="props.phone ? 'items-start px-3 py-2.5 text-sm' : 'items-center px-3 py-1.5'"
    role="status"
    data-testid="mods-safe-mode-banner"
  >
    <PowerOff
      class="h-3.5 w-3.5 shrink-0 text-accent"
      aria-hidden="true"
    />
    <div
      class="flex min-w-0 flex-1 flex-wrap gap-x-2 gap-y-0.5 [&_span]:text-muted"
      :class="{ 'flex-col': props.phone }"
    >
      <strong>Mods are stopped for now</strong>
      <span>Fleet started without mods. They start again when Fleet restarts or you turn them back on.</span>
      <span
        v-if="error"
        class="!text-error"
        role="alert"
        data-testid="mods-safe-mode-error"
      >{{ error }}</span>
    </div>
    <button
      v-if="props.phone"
      type="button"
      class="ph-btn ph-btn--sm ph-btn--outline"
      :disabled="pending"
      data-testid="mods-safe-mode-turn-on"
      @click="turnBackOn"
    >
      {{ pending ? "Turning on…" : "Turn mods back on" }}
    </button>
    <Button
      v-else
      type="button"
      size="sm"
      variant="outline"
      :disabled="pending"
      data-testid="mods-safe-mode-turn-on"
      @click="turnBackOn"
    >
      {{ pending ? "Turning on…" : "Turn mods back on" }}
    </Button>
  </div>
</template>
