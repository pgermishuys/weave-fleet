<script setup lang="ts">
import type { HTMLAttributes } from "vue"
import { cn } from "@/lib/utils"
import { injectMenuHint } from "./hint"

const props = defineProps<{ class?: HTMLAttributes["class"] }>()

const hint = injectMenuHint()
</script>

<template>
  <!-- Rows carry their hint as aria-description, so screen readers hear it with the row, not from here. -->
  <div
    data-slot="context-menu-hint"
    aria-hidden="true"
    :class="cn(
      '-mx-[5px] mt-1 -mb-[5px] h-[50px] overflow-hidden border-t border-border bg-text/[0.025] px-[13px] pt-2 pb-[9px] text-[11.5px] leading-[1.4] text-muted [overflow-wrap:anywhere]',
      props.class,
    )"
  >
    <template v-if="hint">
      {{ hint }}
    </template>
    <slot v-else />
  </div>
</template>
