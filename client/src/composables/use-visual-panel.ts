import { shallowRef, type ShallowRef } from 'vue'
import type { VisualPayload } from '@/lib/visual-payload'

const visualPayloadsBySession = new Map<string, ShallowRef<VisualPayload | null>>()

function getOrCreateEntry(sessionId: string): ShallowRef<VisualPayload | null> {
  let entry = visualPayloadsBySession.get(sessionId)
  if (!entry) {
    entry = shallowRef<VisualPayload | null>(null)
    visualPayloadsBySession.set(sessionId, entry)
  }
  return entry
}

export function useVisualPanel(sessionId: string) {
  const visualPayload = getOrCreateEntry(sessionId)

  function showVisual(payload: VisualPayload): void {
    visualPayload.value = payload
  }

  function clearVisual(): void {
    // Only null the ref — do NOT delete the map entry. Consumer computeds
    // capture this ShallowRef by identity (keyed only on sessionId), so
    // deleting the entry would orphan them: a later useVisualPanel(sessionId)
    // call would create a NEW ref that existing consumers never see.
    // Entries are effectively immortal per session id; payloads are small.
    visualPayload.value = null
  }

  return {
    visualPayload,
    showVisual,
    clearVisual,
  }
}
