<script setup lang="ts">
import type { Component, HTMLAttributes } from "vue"
import { cn } from "@/lib/utils"

// The one empty state: what a list, panel or page shows when it has nothing in it. An optional icon, a title,
// one line of text and an optional action. `bordered` draws the dashed card the Settings lists use; leave it off
// inside a panel that already has its own frame. Words come from the caller: "No … yet" for a list that fills
// over time, "No … found" or "No matching …" for a search.
const props = withDefaults(
  defineProps<{
    title: string
    description?: string
    icon?: Component
    bordered?: boolean
    /** Smaller type, padding and icon, for narrow lists, side panels and popovers. */
    compact?: boolean
    class?: HTMLAttributes["class"]
  }>(),
  { description: undefined, icon: undefined, bordered: false, compact: false, class: undefined },
)
</script>

<template>
  <div
    data-slot="empty-state"
    :class="
      cn(
        'flex flex-col items-center justify-center text-center',
        props.compact ? 'gap-1 px-3 py-4' : 'gap-1 p-6',
        props.bordered && 'rounded-card border border-dashed border-border',
        props.class,
      )
    "
  >
    <component
      :is="props.icon"
      v-if="props.icon"
      :size="props.compact ? 20 : 28"
      class="mb-2 shrink-0 text-muted"
      aria-hidden="true"
    />
    <p
      class="m-0 text-text"
      :class="props.compact ? 'text-sm font-medium' : 'text-base font-semibold'"
    >
      {{ props.title }}
    </p>
    <p
      v-if="props.description || $slots.default"
      class="m-0 max-w-prose text-muted"
      :class="props.compact ? 'text-xs' : 'text-sm'"
    >
      <slot>{{ props.description }}</slot>
    </p>
    <div
      v-if="$slots.action"
      class="mt-3 flex flex-wrap items-center justify-center gap-2"
    >
      <slot name="action" />
    </div>
  </div>
</template>
