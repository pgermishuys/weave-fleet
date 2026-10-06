<script setup lang="ts">
import type { ContextMenuItemEmits, ContextMenuItemProps } from "reka-ui"
import type { HTMLAttributes } from "vue"
import { reactiveOmit } from "@vueuse/core"
import {
  ContextMenuItem,
  useForwardPropsEmits,
} from "reka-ui"
import { cn } from "@/lib/utils"
import { menuItemClass } from "@/components/ui/menu-classes"
import { useMenuRowHint } from "./hint"

const props = withDefaults(defineProps<ContextMenuItemProps & {
  class?: HTMLAttributes["class"]
  inset?: boolean
  variant?: "default" | "destructive"
  /** What the row does, in the menu's footer (ContextMenuHint) while the row is highlighted. */
  hint?: string
}>(), {
  variant: "default",
})
const emits = defineEmits<ContextMenuItemEmits>()

const delegatedProps = reactiveOmit(props, "class", "hint")

const forwarded = useForwardPropsEmits(delegatedProps, emits)
const { onFocus, onBlur } = useMenuRowHint(() => props.hint)
</script>

<template>
  <ContextMenuItem
    data-slot="context-menu-item"
    :data-inset="inset ? '' : undefined"
    :data-variant="variant"
    v-bind="forwarded"
    :aria-description="hint"
    :class="cn(
      menuItemClass,
      props.class,
    )"
    @focus="onFocus"
    @blur="onBlur"
  >
    <slot />
  </ContextMenuItem>
</template>
