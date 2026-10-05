<script setup lang="ts">
import { Check } from "lucide-vue-next";
import BottomSheet from "@/components/phone/BottomSheet.vue";

/** A list to pick one from (agent, model or effort), as a sheet with 44px rows instead of a dropdown. */
export interface ChipOption {
  id: string;
  label: string;
  detail?: string;
}

defineProps<{ open: boolean; title: string; options: readonly ChipOption[]; selected: string }>();
const emit = defineEmits<{ (event: "pick", id: string): void; (event: "close"): void }>();
</script>

<template>
  <BottomSheet
    :open="open"
    :label="title"
    @close="emit('close')"
  >
    <h2 class="pcs__title">
      {{ title }}
    </h2>
    <button
      v-for="option in options"
      :key="option.id"
      type="button"
      class="pcs__row"
      :aria-pressed="option.id === selected"
      @click="emit('pick', option.id); emit('close')"
    >
      <span class="pcs__body">
        <span>{{ option.label }}</span>
        <span
          v-if="option.detail"
          class="pcs__detail"
        >{{ option.detail }}</span>
      </span>
      <Check
        v-if="option.id === selected"
        :size="16"
        class="text-accent"
        aria-hidden="true"
      />
    </button>
  </BottomSheet>
</template>

<style scoped>
.pcs__title {
  margin-bottom: 6px;
  font-size: 15px;
  font-weight: 600;
}

.pcs__row {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  min-height: 48px;
  padding: 6px 2px;
  border: 0;
  border-bottom: 1px solid var(--border);
  background: transparent;
  color: var(--text);
  font: inherit;
  font-size: 14px;
  text-align: left;
}

.pcs__body {
  display: grid;
  flex: 1;
  min-width: 0;
}

.pcs__detail {
  overflow: hidden;
  font-size: 12px;
  color: var(--muted);
  white-space: nowrap;
  text-overflow: ellipsis;
}
</style>
