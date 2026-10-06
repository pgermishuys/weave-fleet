<script setup lang="ts">
import { Check } from "lucide-vue-next";
import BottomSheet from "@/components/phone/BottomSheet.vue";

/** A list to pick one from (agent, model or effort), as a sheet of settings rows with a check on the current one. */
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
    <div class="ph-card">
      <button
        v-for="option in options"
        :key="option.id"
        type="button"
        class="ph-set"
        :aria-pressed="option.id === selected"
        @click="emit('pick', option.id); emit('close')"
      >
        <span class="ph-set__main">
          <span class="ph-set__t">{{ option.label }}</span>
          <span
            v-if="option.detail"
            class="ph-set__s"
          >{{ option.detail }}</span>
        </span>
        <Check
          v-if="option.id === selected"
          class="ph-set__check"
          aria-hidden="true"
        />
        <span
          v-else
          class="ph-set__nocheck"
        />
      </button>
    </div>
  </BottomSheet>
</template>

