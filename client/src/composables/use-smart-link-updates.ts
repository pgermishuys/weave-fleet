import { onMounted, onUnmounted } from "vue"
import { onDomainEvent } from "@/composables/on-domain-event"
import { onReconnect } from "@/composables/use-signalr-socket"
import { liveTarget } from "@/lib/machine-target"
import { useSmartLinksStore } from "@/stores/smart-links"
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
    unsubscribe = onDomainEvent(liveTarget(), "sessions", SMART_LINK_UPDATED, (event) => {
      const wire: SmartLinkWire | undefined = event.payload
      if (!wire?.id || !wire.sessionId) return

      store.applyPushed(wire)
    })
    unsubscribeReconnect = onReconnect(liveTarget(), () => {
      void store.reloadHeaderLinks()
    })
  })

  onUnmounted(() => {
    unsubscribe?.()
    unsubscribeReconnect?.()
  })
}
