<script setup lang="ts">
import type { TabsTriggerProps } from "reka-ui"
import type { HTMLAttributes } from "vue"
import { reactiveOmit } from "@vueuse/core"
import { TabsTrigger, useForwardProps } from "reka-ui"
import { cn } from "@/lib/utils"

const props = defineProps<TabsTriggerProps & { class?: HTMLAttributes["class"] }>()

const delegatedProps = reactiveOmit(props, "class")

const forwardedProps = useForwardProps(delegatedProps)
</script>

<template>
  <TabsTrigger
    data-slot="tabs-trigger"
    :class="cn(
      'data-[state=active]:bg-background dark:data-[state=active]:text-foreground focus-visible:border-ring focus-visible:ring-ring/50 focus-visible:outline-ring dark:data-[state=active]:border-input dark:data-[state=active]:bg-input/30 text-foreground dark:text-muted-foreground inline-flex h-[calc(100%-1px)] flex-1 items-center justify-center gap-1.5 border border-transparent px-2 py-1 text-sm font-medium whitespace-nowrap transition-[color,box-shadow] focus-visible:ring-[3px] focus-visible:outline-1 disabled:pointer-events-none disabled:opacity-50 data-[state=active]:shadow-sm [&_svg]:pointer-events-none [&_svg]:shrink-0 [&_svg:not([class*=\'size-\'])]:size-4',
      props.class,
    )"
    v-bind="forwardedProps"
  >
    <slot />
  </TabsTrigger>
</template>

<style>
/* Underline variant: override default trigger styling when parent has data-variant="underline" */
[data-variant="underline"] > [data-slot="tabs-trigger"] {
  height: auto;
  flex: none;
  border: none;
  border-radius: 0;
  background: transparent !important;
  box-shadow: none !important;
  padding: 8px 2px;
  margin-bottom: -1px;
  border-bottom: 2px solid transparent;
  color: var(--muted);
  font-size: 13px;
  font-weight: 500;
  transition: color 0.15s, border-color 0.15s;
}

[data-variant="underline"] > [data-slot="tabs-trigger"]:hover {
  color: var(--text);
}

[data-variant="underline"] > [data-slot="tabs-trigger"][data-state="active"] {
  color: var(--text);
  border-bottom-color: var(--accent);
  background: transparent !important;
}
</style>
