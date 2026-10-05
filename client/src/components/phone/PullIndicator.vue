<script setup lang="ts">
import { shallowRef } from "vue";
import { RefreshCw } from "lucide-vue-next";
import { phoneLook } from "@/composables/phone/use-phone-env";

/** The pull-to-refresh indicator: iOS's eight spokes (as many as the pull has filled), or Material's round arrow. */
defineProps<{ spokes: number; refreshing: boolean }>();
const indicator = shallowRef<HTMLElement | null>(null);
defineExpose({ indicator });
</script>

<template>
  <div
    class="ph-ptr"
    aria-hidden="true"
  >
    <svg
      v-if="phoneLook === 'ios'"
      :ref="(el) => (indicator = el as HTMLElement | null)"
      class="ph-ptr__ios"
      :class="{ 'is-spinning': refreshing }"
      viewBox="0 0 28 28"
    >
      <line
        v-for="i in 8"
        :key="i"
        x1="14"
        y1="3.5"
        x2="14"
        y2="8.5"
        :transform="`rotate(${(i - 1) * 45} 14 14)`"
        :opacity="0.25 + ((i - 1) / 8) * 0.75"
        :visibility="refreshing || i <= spokes ? 'visible' : 'hidden'"
      />
    </svg>
    <div
      v-else
      :ref="(el) => (indicator = el as HTMLElement | null)"
      class="ph-ptr__android"
      :class="{ 'is-spinning': refreshing }"
    >
      <RefreshCw
        :size="22"
        :stroke-width="2.6"
      />
    </div>
  </div>
</template>
