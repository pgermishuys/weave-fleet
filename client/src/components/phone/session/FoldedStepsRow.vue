<script setup lang="ts">
import { ChevronRight, LoaderCircle } from "lucide-vue-next";

/** A run of tool calls as one row: "Read 4 files · edited 1 · searched 2 ›". Tapping lists them. */
defineProps<{ summary: string; running: boolean; failed: number; count: number }>();
const emit = defineEmits<{ (event: "open"): void }>();
</script>

<template>
  <button
    type="button"
    class="ph-step"
    data-testid="phone-steps-row"
    @click="emit('open')"
  >
    <LoaderCircle
      v-if="running"
      class="ph-spinner"
      :size="18"
      aria-hidden="true"
    />
    <span class="ph-step__t">{{ summary }}<span
      v-if="failed"
      class="ph-step__failed"
    > · {{ failed }} failed</span></span>
    <ChevronRight
      class="ph-row__chev"
      :size="16"
      :stroke-width="3"
      aria-hidden="true"
    />
  </button>
</template>
