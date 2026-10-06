<script setup lang="ts">
import type { DropdownMenuSubContentEmits, DropdownMenuSubContentProps } from "reka-ui"
import type { HTMLAttributes } from "vue"
import { reactiveOmit } from "@vueuse/core"
import {
  DropdownMenuSubContent,
  useForwardPropsEmits,
} from "reka-ui"
import { cn } from "@/lib/utils"
import { menuContentClass } from "@/components/ui/menu-classes"

const props = defineProps<DropdownMenuSubContentProps & { class?: HTMLAttributes["class"] }>()
const emits = defineEmits<DropdownMenuSubContentEmits>()

const delegatedProps = reactiveOmit(props, "class")

const forwarded = useForwardPropsEmits(delegatedProps, emits)
</script>

<template>
  <DropdownMenuSubContent
    data-slot="dropdown-menu-sub-content"
    v-bind="forwarded"
    :class="cn(menuContentClass, 'origin-(--reka-dropdown-menu-content-transform-origin)', props.class)"
  >
    <slot />
  </DropdownMenuSubContent>
</template>
