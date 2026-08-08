import { shallowRef } from 'vue'
import type { VisualPayload } from '@/lib/visual-payload'

const visualPayload = shallowRef<VisualPayload | null>(null)

export function useVisualPanel() {
  function showVisual(payload: VisualPayload): void {
    visualPayload.value = payload
  }

  function clearVisual(): void {
    visualPayload.value = null
  }

  return {
    visualPayload,
    showVisual,
    clearVisual,
  }
}
