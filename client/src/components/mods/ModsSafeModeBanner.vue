<script setup lang="ts">
import { computed, onMounted, shallowRef } from "vue";
import { PowerOff } from "lucide-vue-next";
import { Button } from "@/components/ui/button";
import { useModsStore } from "@/stores/mods";

/**
 * Says mods are stopped while safe mode is on ("Start without mods", `?mods=off`), and turns them back on.
 * Nothing at all otherwise, so it takes no room from the panels below.
 */
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
    class="mods-safe-banner"
    role="status"
    data-testid="mods-safe-mode-banner"
  >
    <PowerOff
      class="mods-safe-banner__icon"
      aria-hidden="true"
    />
    <div class="mods-safe-banner__text">
      <strong>Mods are stopped for now</strong>
      <span>Fleet started without mods. They start again when Fleet restarts or you turn them back on.</span>
      <span
        v-if="error"
        class="mods-safe-banner__error"
        role="alert"
        data-testid="mods-safe-mode-error"
      >{{ error }}</span>
    </div>
    <Button
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

<style scoped>
.mods-safe-banner {
  display: flex;
  flex-shrink: 0;
  align-items: center;
  gap: 10px;
  padding: 6px 12px;
  border-bottom: 1px solid var(--border);
  background: var(--panel-bg);
  color: var(--foreground);
  font-size: 0.8125rem;
}

.mods-safe-banner__icon {
  width: 14px;
  height: 14px;
  flex-shrink: 0;
  color: var(--accent);
}

.mods-safe-banner__text {
  display: flex;
  min-width: 0;
  flex: 1;
  flex-wrap: wrap;
  gap: 2px 8px;
}

.mods-safe-banner__text span {
  color: var(--muted-foreground);
}

.mods-safe-banner__text .mods-safe-banner__error {
  color: var(--error);
}
</style>
