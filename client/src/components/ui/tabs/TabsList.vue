<script setup lang="ts">
import type { TabsListProps } from "reka-ui"
import type { HTMLAttributes } from "vue"
import { reactiveOmit } from "@vueuse/core"
import { TabsList } from "reka-ui"
import { cn } from "@/lib/utils"

const props = defineProps<TabsListProps & { class?: HTMLAttributes["class"]; variant?: "default" | "underline" }>()

const delegatedProps = reactiveOmit(props, "class", "variant")
</script>

<template>
  <TabsList
    data-slot="tabs-list"
    v-bind="delegatedProps"
    :class="cn(
      'inline-flex w-fit items-center justify-center',
      props.variant === 'underline'
        ? 'h-10 gap-4 border-b border-border bg-transparent px-0'
        : 'bg-muted text-muted-foreground h-9 p-[3px]',
      props.class,
    )"
    :data-variant="props.variant ?? 'default'"
  >
    <slot />
  </TabsList>
</template>
