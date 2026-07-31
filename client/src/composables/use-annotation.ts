import { ref, computed } from 'vue'
import type { AnnotationAnchor } from '@/lib/annotation-types'
import { extractAnchorText } from '@/lib/annotation-types'

export interface PopoverPosition {
  x: number
  y: number
}

export interface UseAnnotationOptions {
  onSubmit: (text: string) => void
}

/**
 * Composable managing annotation lifecycle state.
 * Handles anchor tracking, popover visibility, and submission flow.
 */
export function useAnnotation(options: UseAnnotationOptions) {
  const { onSubmit } = options

  const activeAnchor = ref<AnnotationAnchor | null>(null)
  const popoverPosition = ref<PopoverPosition>({ x: 0, y: 0 })

  const isPopoverOpen = computed(() => activeAnchor.value !== null)

  /**
   * Opens the annotation popover for the given anchor at the specified position.
   */
  function openAnnotation(anchor: AnnotationAnchor, position: PopoverPosition) {
    activeAnchor.value = anchor
    popoverPosition.value = { ...position }
  }

  /**
   * Closes the annotation popover and resets state.
   */
  function closeAnnotation() {
    activeAnchor.value = null
    popoverPosition.value = { x: 0, y: 0 }
  }

  /**
   * Submits the annotation with the user's text, calls the callback, then closes.
   */
  function submitAnnotation(text: string) {
    if (!activeAnchor.value) return

    const anchorText = extractAnchorText(activeAnchor.value)
    const formattedText = `${text}\n\nContext: ${anchorText}`

    onSubmit(formattedText)
    closeAnnotation()
  }

  return {
    activeAnchor,
    isPopoverOpen,
    popoverPosition,
    openAnnotation,
    closeAnnotation,
    submitAnnotation,
  }
}
