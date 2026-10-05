<script setup lang="ts">
import { Check } from "lucide-vue-next";
import BottomSheet from "@/components/phone/BottomSheet.vue";

/** A list to pick one from (agent, model or effort), as a sheet of rows with a check on the current one. */
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
    :title="title"
    @close="emit('close')"
  >
    <div class="ph-group">
      <button
        v-for="option in options"
        :key="option.id"
        type="button"
        class="ph-row"
        :aria-pressed="option.id === selected"
        @click="emit('pick', option.id); emit('close')"
      >
        <span class="ph-row__main">
          <span class="ph-row__title">{{ option.label }}</span>
          <span
            v-if="option.detail"
            class="ph-row__sub"
          >{{ option.detail }}</span>
        </span>
        <Check
          v-if="option.id === selected"
          class="ph-row__check"
          :size="22"
          :stroke-width="2.6"
          aria-hidden="true"
        />
      </button>
    </div>
  </BottomSheet>
</template>

<style scoped>
.ph-row__title,
.ph-row__sub {
  display: block;
}
</style>
