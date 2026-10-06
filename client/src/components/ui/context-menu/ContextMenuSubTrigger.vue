<script setup lang="ts">
import type { ContextMenuSubTriggerProps } from "reka-ui"
import type { HTMLAttributes } from "vue"
import { reactiveOmit } from "@vueuse/core"
import { ChevronRightIcon } from '@radix-icons/vue'
import {
  ContextMenuSubTrigger,
  useForwardProps,
} from "reka-ui"
import { cn } from "@/lib/utils"
import { menuSubTriggerClass } from "@/components/ui/menu-classes"
import { useMenuRowHint } from "./hint"

const props = defineProps<ContextMenuSubTriggerProps & {
  class?: HTMLAttributes["class"]
  inset?: boolean
  /** What the submenu is for, in the menu's footer (ContextMenuHint); it stays while the submenu is open. */
  hint?: string
}>()

const delegatedProps = reactiveOmit(props, "class", "hint")

const forwardedProps = useForwardProps(delegatedProps)
const { onFocus, onBlur } = useMenuRowHint(() => props.hint, (row) => row.dataset.state === "open")
</script>

<template>
  <ContextMenuSubTrigger
    data-slot="context-menu-sub-trigger"
    :data-inset="inset ? '' : undefined"
    v-bind="forwardedProps"
    :aria-description="hint"
    :class="cn(
      menuSubTriggerClass,
      props.class,
    )"
    @focus="onFocus"
    @blur="onBlur"
  >
    <slot />
    <ChevronRightIcon class="ml-auto" />
  </ContextMenuSubTrigger>
</template>
