<script setup lang="ts">
import type { HTMLAttributes } from "vue"
import { LoaderCircle } from "lucide-vue-next"
import { cn } from "@/lib/utils"

// The one spinner. Four sizes: xs 12px (inside a chip or a dense row), sm 14px (inside a small button), md 16px (the
// default: a button, a row, beside "Loading…"), lg 20px (a panel or page that is loading). With prefers-reduced-motion
// it doesn't turn; it fades slowly instead, so it still says "working" without moving.
// Give it a `label` when nothing next to it says what is loading; otherwise it is hidden from screen readers.
const SIZES = { xs: 12, sm: 14, md: 16, lg: 20 } as const

const props = withDefaults(
  defineProps<{
    size?: keyof typeof SIZES
    label?: string
    class?: HTMLAttributes["class"]
  }>(),
  { size: "md", label: undefined, class: undefined },
)
</script>

<template>
  <LoaderCircle
    data-slot="spinner"
    :size="SIZES[props.size]"
    :class="cn('shrink-0 motion-safe:animate-spin motion-reduce:animate-pulse', props.class)"
    :role="props.label ? 'status' : undefined"
    :aria-label="props.label"
    :aria-hidden="props.label ? undefined : 'true'"
  />
</template>
