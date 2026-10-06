<script setup lang="ts">
import type { DropdownMenuSubContentEmits, DropdownMenuSubContentProps } from "reka-ui"
import type { HTMLAttributes } from "vue"
import { reactiveOmit } from "@vueuse/core"
import {
  ContextMenuSubContent,
  useForwardPropsEmits,
} from "reka-ui"
import { cn } from "@/lib/utils"
import { menuContentClass } from "@/components/ui/menu-classes"
import { provideNoMenuHint } from "./hint"

const props = defineProps<DropdownMenuSubContentProps & { class?: HTMLAttributes["class"] }>()
const emits = defineEmits<DropdownMenuSubContentEmits>()

const delegatedProps = reactiveOmit(props, "class")

const forwarded = useForwardPropsEmits(delegatedProps, emits)

provideNoMenuHint()
</script>

<template>
  <ContextMenuSubContent
    data-slot="context-menu-sub-content"
    v-bind="forwarded"
    :class="
      cn(
        menuContentClass,
        'origin-(--reka-context-menu-content-transform-origin)',
        props.class,
      )
    "
  >
    <slot />
  </ContextMenuSubContent>
</template>
