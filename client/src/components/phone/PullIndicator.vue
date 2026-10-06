<script setup lang="ts">
import { shallowRef } from "vue";

/**
 * The pull-to-refresh indicator: Fleet's Working glyph. Its four dots fill in, clockwise from the top left, as you
 * pull; once let go it ticks while Fleet refreshes.
 */
defineProps<{ dots: number; refreshing: boolean }>();
const indicator = shallowRef<HTMLElement | null>(null);
defineExpose({ indicator });

// Grid order (row by row) against the clockwise order they fill in.
const CLOCKWISE = [0, 1, 3, 2];
</script>

<template>
  <div
    class="ph-ptr"
    aria-hidden="true"
    data-testid="phone-pull"
  >
    <span
      :ref="(el) => (indicator = el as HTMLElement | null)"
      class="ph-ptr__quad"
      :class="{ 'ph-ptr__quad--ticking': refreshing }"
    >
      <i
        v-for="slot in 4"
        :key="slot"
        :class="{ 'is-on': CLOCKWISE.indexOf(slot - 1) < dots }"
      />
    </span>
  </div>
</template>
