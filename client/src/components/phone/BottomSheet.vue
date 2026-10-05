<script setup lang="ts">
import { onUnmounted, watch } from "vue";

/**
 * A sheet that slides up from the bottom of the phone screen over a dimmed page. Tapping the dim, or Escape, closes
 * it. Inside the safe area, and never taller than 88% of the screen; its body scrolls.
 */
const props = defineProps<{ open: boolean; label: string; full?: boolean }>();
const emit = defineEmits<{ (event: "close"): void }>();

function onKey(event: KeyboardEvent): void {
  if (event.key === "Escape") emit("close");
}

watch(() => props.open, (open) => {
  if (open) document.addEventListener("keydown", onKey);
  else document.removeEventListener("keydown", onKey);
}, { immediate: true });

onUnmounted(() => document.removeEventListener("keydown", onKey));
</script>

<template>
  <Teleport to="body">
    <div
      v-if="open"
      class="sheet-layer"
    >
      <div
        class="sheet-scrim"
        aria-hidden="true"
        @click="emit('close')"
      />
      <section
        class="sheet"
        :class="{ 'sheet--full': full }"
        role="dialog"
        aria-modal="true"
        :aria-label="label"
      >
        <div
          class="sheet__grab"
          aria-hidden="true"
        />
        <div class="sheet__body">
          <slot />
        </div>
      </section>
    </div>
  </Teleport>
</template>

<style scoped>
.sheet-layer {
  position: fixed;
  inset: 0;
  z-index: 60;
}

.sheet-scrim {
  position: absolute;
  inset: 0;
  background: rgb(0 0 0 / 0.38);
}

.sheet {
  position: absolute;
  right: 0;
  bottom: 0;
  left: 0;
  display: flex;
  flex-direction: column;
  max-height: 88dvh;
  padding-bottom: env(safe-area-inset-bottom);
  border-top-left-radius: var(--radius-panel);
  border-top-right-radius: var(--radius-panel);
  background: var(--panel-bg);
  color: var(--text);
  box-shadow: 0 -12px 40px -18px rgb(0 0 0 / 0.45);
}

.sheet--full {
  height: 92dvh;
  max-height: 92dvh;
}

.sheet__grab {
  width: 36px;
  height: 4px;
  margin: 8px auto 4px;
  flex: none;
  border-radius: 999px;
  background: var(--border);
}

.sheet__body {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-height: 0;
  overflow-y: auto;
  padding: 6px 16px 16px;
}
</style>
