import { defineStore } from 'pinia'
import { ref } from 'vue'
import type { SmartLink } from '@/plugins/builtin/smart-links/types'

/** Wire format from API — has metadataJson instead of metadata */
interface SmartLinkWire {
  id: string
  sessionId: string
  url: string
  providerId: string
  resourceType: string
  resourceId: string
  title: string
  status: string
  statusLabel: string
  metadataJson: string | null
  isDismissed: boolean
  isTerminal: boolean
  createdAt: string
  updatedAt: string
}

function parseWireLink(wire: SmartLinkWire): SmartLink {
  let metadata: Record<string, unknown> = {}
  if (wire.metadataJson) {
    try {
      metadata = JSON.parse(wire.metadataJson)
    } catch {
      // ignore malformed JSON
    }
  }
  return {
    id: wire.id,
    sessionId: wire.sessionId,
    url: wire.url,
    providerId: wire.providerId,
    resourceType: wire.resourceType,
    resourceId: wire.resourceId,
    title: wire.title,
    status: wire.status,
    statusLabel: wire.statusLabel,
    metadata,
    isDismissed: wire.isDismissed,
    isTerminal: wire.isTerminal,
    createdAt: wire.createdAt,
    updatedAt: wire.updatedAt,
  }
}

export const useSmartLinksStore = defineStore('smart-links', () => {
  // Map from sessionId → SmartLink[]
  const linksBySession = ref(new Map<string, SmartLink[]>())

  function setLinks(sessionId: string, links: SmartLinkWire[]): void {
    linksBySession.value.set(sessionId, links.map(parseWireLink))
  }

  function upsertLink(wire: SmartLinkWire): void {
    const link = parseWireLink(wire)
    const existing = linksBySession.value.get(link.sessionId) ?? []
    const idx = existing.findIndex((l) => l.id === link.id)
    if (idx >= 0) {
      existing[idx] = link
    } else {
      existing.push(link)
    }
    linksBySession.value.set(link.sessionId, [...existing])
  }

  function dismissLink(sessionId: string, linkId: string): void {
    const existing = linksBySession.value.get(sessionId)
    if (!existing) return
    linksBySession.value.set(
      sessionId,
      existing.map((l) => (l.id === linkId ? { ...l, isDismissed: true } : l)),
    )
  }

  function getActiveLinks(sessionId: string): SmartLink[] {
    return (linksBySession.value.get(sessionId) ?? []).filter((l) => !l.isDismissed)
  }

  function getAllLinks(sessionId: string): SmartLink[] {
    return linksBySession.value.get(sessionId) ?? []
  }

  function isUrlDismissed(sessionId: string, url: string): boolean {
    return (linksBySession.value.get(sessionId) ?? []).some(
      (l) => l.url === url && l.isDismissed,
    )
  }

  function isUrlKnown(sessionId: string, url: string): boolean {
    return (linksBySession.value.get(sessionId) ?? []).some((l) => l.url === url)
  }

  return {
    linksBySession,
    setLinks,
    upsertLink,
    dismissLink,
    getActiveLinks,
    getAllLinks,
    isUrlDismissed,
    isUrlKnown,
  }
})
