<script setup lang="ts">
import type { ContextMenuContentEmits, ContextMenuContentProps } from "reka-ui"
import type { HTMLAttributes } from "vue"
import { reactiveOmit } from "@vueuse/core"
import {
  ContextMenuContent,
  ContextMenuPortal,
  useForwardPropsEmits,
} from "reka-ui"
import { cn } from "@/lib/utils"
import { menuContentClass } from "@/components/ui/menu-classes"
import { provideMenuHint } from "./hint"

defineOptions({
  inheritAttrs: false,
})

const props = defineProps<ContextMenuContentProps & { class?: HTMLAttributes["class"] }>()
const emits = defineEmits<ContextMenuContentEmits>()

const delegatedProps = reactiveOmit(props, "class")

const forwarded = useForwardPropsEmits(delegatedProps, emits)

provideMenuHint()
</script>

<template>
  <ContextMenuPortal>
    <ContextMenuContent
      data-slot="context-menu-content"
      v-bind="{ ...$attrs, ...forwarded }"
      :class="cn(
        menuContentClass,
        'origin-(--reka-context-menu-content-transform-origin) max-h-(--reka-context-menu-content-available-height)',
        props.class,
      )"
    >
      <slot />
    </ContextMenuContent>
  </ContextMenuPortal>
</template>
