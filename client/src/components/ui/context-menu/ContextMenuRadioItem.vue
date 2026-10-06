<script setup lang="ts">
import type { ContextMenuRadioItemEmits, ContextMenuRadioItemProps } from "reka-ui"
import type { HTMLAttributes } from "vue"
import { reactiveOmit } from "@vueuse/core"
import { DotFilledIcon } from '@radix-icons/vue'
import {
  ContextMenuItemIndicator,
  ContextMenuRadioItem,
  useForwardPropsEmits,
} from "reka-ui"
import { cn } from "@/lib/utils"
import { menuIndicatorItemClass } from "@/components/ui/menu-classes"

const props = defineProps<ContextMenuRadioItemProps & { class?: HTMLAttributes["class"] }>()
const emits = defineEmits<ContextMenuRadioItemEmits>()

const delegatedProps = reactiveOmit(props, "class")

const forwarded = useForwardPropsEmits(delegatedProps, emits)
</script>

<template>
  <ContextMenuRadioItem
    data-slot="context-menu-radio-item"
    v-bind="forwarded"
    :class="cn(
      menuIndicatorItemClass,
      props.class,
    )"
  >
    <span class="pointer-events-none absolute left-2 flex size-3.5 items-center justify-center">
      <ContextMenuItemIndicator>
        <slot name="indicator-icon">
          <DotFilledIcon class="size-2 fill-current" />
        </slot>
      </ContextMenuItemIndicator>
    </span>
    <slot />
  </ContextMenuRadioItem>
</template>
