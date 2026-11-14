<script setup lang="ts">
import type { HTMLAttributes } from "vue"
import { AlertCircle } from "lucide-vue-next"
import { Button } from "@/components/ui/button"
import { cn } from "@/lib/utils"

// The one inline error: what went wrong, an optional reason (usually the server's message) and, when the caller
// listens for `retry`, a "Try again" button. The words are in --error-text on a light --error tint, which every theme
// keeps at 4.5:1. `compact` drops the tint for a one-line note under a field or inside a row.
const props = withDefaults(
  defineProps<{
    /** What went wrong, e.g. "Couldn't load automations." When that's all there is, the error itself. */
    message?: string | null
    /** Why, when known: the server's or the harness's own words. */
    reason?: string | null
    /** Disables "Try again" while the retry runs. */
    retrying?: boolean
    compact?: boolean
    /** Listening for `retry` shows "Try again". */
    onRetry?: () => void
    class?: HTMLAttributes["class"]
  }>(),
  { message: undefined, reason: undefined, retrying: false, compact: false, onRetry: undefined, class: undefined },
)
</script>

<template>
  <div
    data-slot="error-note"
    role="alert"
    :class="
      cn(
        'flex items-start gap-2 text-error-text',
        props.compact ? 'text-xs' : 'rounded-card border border-error/30 bg-error/10 px-3 py-2 text-sm',
        props.class,
      )
    "
  >
    <AlertCircle
      :size="props.compact ? 14 : 16"
      class="mt-px shrink-0"
      aria-hidden="true"
    />
    <div class="min-w-0 flex-1 break-words">
      <p
        v-if="props.message || $slots.default"
        class="m-0"
        :class="props.reason && 'font-medium'"
      >
        <slot>{{ props.message }}</slot>
      </p>
      <p
        v-if="props.reason"
        class="m-0 mt-0.5"
      >
        {{ props.reason }}
      </p>
      <slot name="details" />
      <Button
        v-if="props.onRetry"
        size="sm"
        variant="outline"
        class="mt-2"
        :disabled="props.retrying"
        @click="props.onRetry()"
      >
        Try again
      </Button>
    </div>
  </div>
</template>
