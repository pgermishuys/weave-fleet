<script setup lang="ts">
import { computed, onMounted, ref, shallowRef } from "vue"
import { Button } from "@/components/ui/button"
import { Textarea } from "@/components/ui/textarea"

interface Props {
  x: number
  y: number
  anchorText: string
}

const props = defineProps<Props>()

const emit = defineEmits<{
  send: [text: string]
  cancel: []
}>()

const textareaRef = shallowRef<InstanceType<typeof Textarea> | null>(null)
const popoverRef = shallowRef<HTMLDivElement | null>(null)
const annotationText = ref("")

const POPOVER_WIDTH = 400
const POPOVER_MAX_HEIGHT = 300
const VIEWPORT_PADDING = 16

const clampedPosition = computed(() => {
  const viewportWidth = window.innerWidth
  const viewportHeight = window.innerHeight

  let clampedX = props.x
  let clampedY = props.y

  // Clamp X to keep popover within viewport
  if (clampedX + POPOVER_WIDTH > viewportWidth - VIEWPORT_PADDING) {
    clampedX = viewportWidth - POPOVER_WIDTH - VIEWPORT_PADDING
  }
  if (clampedX < VIEWPORT_PADDING) {
    clampedX = VIEWPORT_PADDING
  }

  // Clamp Y to keep popover within viewport
  if (clampedY + POPOVER_MAX_HEIGHT > viewportHeight - VIEWPORT_PADDING) {
    clampedY = viewportHeight - POPOVER_MAX_HEIGHT - VIEWPORT_PADDING
  }
  if (clampedY < VIEWPORT_PADDING) {
    clampedY = VIEWPORT_PADDING
  }

  return {
    left: `${clampedX}px`,
    top: `${clampedY}px`,
  }
})

const truncatedAnchorText = computed(() => {
  const maxLength = 120
  if (props.anchorText.length <= maxLength) {
    return props.anchorText
  }
  return `${props.anchorText.slice(0, maxLength)}…`
})

const canSend = computed(() => annotationText.value.trim().length > 0)

function handleSend(): void {
  if (!canSend.value) {
    return
  }
  emit("send", annotationText.value.trim())
}

function handleCancel(): void {
  emit("cancel")
}

function handleEscape(event: KeyboardEvent): void {
  if (event.key === "Escape") {
    event.preventDefault()
    event.stopPropagation()
    handleCancel()
  }
}

onMounted(() => {
  const el = textareaRef.value?.$el as HTMLTextAreaElement | undefined
  el?.focus()
})
</script>

<template>
  <div
    ref="popoverRef"
    class="fixed z-50 flex w-[400px] flex-col gap-3 border border-border bg-card p-4 shadow-xl shadow-black/50 ring-1 ring-white/[0.08]"
    :style="clampedPosition"
    @keydown="handleEscape"
  >
    <!-- Anchor text preview -->
    <blockquote
      class="border-l-2 border-primary/40 bg-muted/30 px-3 py-2 text-sm italic text-muted-foreground"
    >
      "{{ truncatedAnchorText }}"
    </blockquote>

    <!-- Textarea for annotation input -->
    <Textarea
      ref="textareaRef"
      v-model="annotationText"
      placeholder="Add your annotation..."
      class="min-h-24 resize-none"
      @keydown.enter.ctrl="handleSend"
      @keydown.enter.meta="handleSend"
    />

    <!-- Action buttons -->
    <div class="flex items-center justify-end gap-2">
      <Button
        type="button"
        variant="outline"
        size="sm"
        @click="handleCancel"
      >
        Cancel
      </Button>
      <Button
        type="button"
        size="sm"
        :disabled="!canSend"
        @click="handleSend"
      >
        Send
      </Button>
    </div>
  </div>
</template>
