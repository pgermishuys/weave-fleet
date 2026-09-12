import { onMounted, onUnmounted } from "vue"
import { onGlobalEvent } from "@/composables/use-signalr-socket"
import { useSmartLinksStore } from "@/stores/smart-links"
import type { DomainEvent } from "@/lib/domain-events"
import type { SmartLinkWire } from "@/lib/smart-links"

/** Event the server's smart link watcher pushes on the "sessions" topic when a link changes. */
export const SMART_LINK_UPDATED = "smart_link.updated"

/**
 * Applies pushed smart link changes to the store, for every session, so header chips and the
 * Context tab stay current without polling.
 */
export function useSmartLinkUpdates(): void {
  const store = useSmartLinksStore()
  let unsubscribe: (() => void) | null = null

  onMounted(() => {
    unsubscribe = onGlobalEvent("sessions", (event: DomainEvent) => {
      if ((event.type as string) !== SMART_LINK_UPDATED) return
      const wire = event.payload as unknown as SmartLinkWire | undefined
      if (!wire?.id || !wire.sessionId) return

      // Sessions that were never opened load their full list on first view instead.
      if (store.bySession[wire.sessionId]) store.upsertLink(wire)
    })
  })

  onUnmounted(() => {
    unsubscribe?.()
  })
}
