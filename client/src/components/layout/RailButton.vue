<script setup lang="ts">
import type { Component } from "vue";

export interface RailButtonItem {
  id: string;
  label: string;
  icon?: Component;
  isOff?: boolean;
  badge?: number;
}

defineProps<{
  item: RailButtonItem;
  active: boolean;
  /** The phone drawer's 48px rail: no label, the name shows as a tooltip instead. */
  iconOnly?: boolean;
}>();

defineEmits<{ select: [item: never] }>();
</script>

<template>
  <button
    type="button"
    class="rail-item"
    :class="{ active, off: item.isOff, 'icon-only': iconOnly }"
    :data-tooltip="iconOnly ? item.label : undefined"
    :aria-label="item.label"
    :data-off="item.isOff ? 'true' : undefined"
    @click="$emit('select', item as never)"
  >
    <span class="rail-glyph">
      <component
        :is="item.icon"
        :size="18"
        aria-hidden="true"
      />
      <span
        v-if="item.isOff"
        class="rail-off-dot"
        title="Switched off"
        aria-hidden="true"
      />
    </span>
    <span
      v-if="!iconOnly"
      class="rail-label"
    >{{ item.label }}</span>
    <span
      v-if="item.badge"
      class="rail-badge"
      aria-hidden="true"
    >
      {{ item.badge }}
    </span>
  </button>
</template>
