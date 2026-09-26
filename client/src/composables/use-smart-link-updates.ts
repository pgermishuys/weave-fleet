import { onMounted, onUnmounted } from "vue"
import { onGlobalEvent, onReconnect } from "@/composables/use-signalr-socket"
import { useSmartLinksStore } from "@/stores/smart-links"
import type { DomainEvent } from "@/lib/domain-events"
import type { SmartLinkWire } from "@/lib/smart-links"

/** Event the server's smart link watcher pushes on the "sessions" topic when a link changes. */
export const SMART_LINK_UPDATED = "smart_link.updated"

/**
 * Loads every session's header links (the sessions list's pull request badges) and applies pushed changes,
 * for every session, so badges, header pills and the Context tab stay current without polling. After a
 * reconnect the links load again, since changes pushed meanwhile were missed.
 */
export function useSmartLinkUpdates(): void {
  const store = useSmartLinksStore()
  let unsubscribe: (() => void) | null = null
  let unsubscribeReconnect: (() => void) | null = null

  onMounted(() => {
    void store.ensureHeaderLinksLoaded()
    unsubscribe = onGlobalEvent("sessions", (event: DomainEvent) => {
      if ((event.type as string) !== SMART_LINK_UPDATED) return
      const wire = event.payload as unknown as SmartLinkWire | undefined
      if (!wire?.id || !wire.sessionId) return

      store.applyPushed(wire)
    })
    unsubscribeReconnect = onReconnect(() => {
      void store.reloadHeaderLinks()
    })
  })

  onUnmounted(() => {
    unsubscribe?.()
    unsubscribeReconnect?.()
  })
}
