<script setup lang="ts">
import { ChevronRight, LoaderCircle } from "lucide-vue-next";

/** A run of tool calls as one row: "Read 4 files · edited 1 · searched 2 ›". Tapping lists them. */
defineProps<{ summary: string; running: boolean; failed: number; count: number }>();
const emit = defineEmits<{ (event: "open"): void }>();
</script>

<template>
  <button
    type="button"
    class="fsr"
    data-testid="phone-steps-row"
    @click="emit('open')"
  >
    <LoaderCircle
      v-if="running"
      class="animate-spin"
      :size="13"
      aria-hidden="true"
    />
    <span class="fsr__text">{{ summary }}</span>
    <span
      v-if="failed"
      class="fsr__failed"
    >· {{ failed }} failed</span>
    <ChevronRight
      class="fsr__chev"
      :size="14"
      aria-hidden="true"
    />
  </button>
</template>

<style scoped>
.fsr {
  display: flex;
  align-items: center;
  gap: 6px;
  width: 100%;
  min-height: 44px;
  padding: 8px 12px;
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  background: var(--card-bg);
  color: var(--text);
  font: inherit;
  font-size: 13px;
  text-align: left;
  cursor: pointer;
}

.fsr__text {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  white-space: nowrap;
  text-overflow: ellipsis;
}

.fsr__failed {
  color: var(--error);
}

.fsr__chev {
  color: var(--muted);
}
</style>
