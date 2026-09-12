import { defineStore } from "pinia"
import { shallowRef } from "vue"
import { apiFetch } from "@/lib/api-client"
import { isHeaderLink, isVisibleLink, parseWireLink, type SmartLink, type SmartLinkWire } from "@/lib/smart-links"

/** A request from a header chip to show one link in the Context tab. */
export interface SmartLinkFocusRequest {
  sessionId: string
  /** A link id, or "origin" for the session's starting point when it isn't a link (an automation). */
  target: string
  /** Increments on every request, so asking for the same link twice still flashes it. */
  nonce: number
}

// Header order: where the session came from, what it made, then what the user pinned.
const HEADER_ORDER = { origin: 0, own: 1, pinned: 2, mentioned: 3 } as const

const path = (sessionId: string, suffix = "") =>
  `/api/sessions/${encodeURIComponent(sessionId)}/smart-links${suffix}`

export const useSmartLinksStore = defineStore("smart-links", () => {
  const bySession = shallowRef<Record<string, SmartLink[]>>({})
  const focusRequest = shallowRef<SmartLinkFocusRequest | null>(null)
  const loading = new Map<string, Promise<void>>()
  let focusNonce = 0

  function replace(sessionId: string, links: SmartLink[]): void {
    bySession.value = { ...bySession.value, [sessionId]: links }
  }

  function setLinks(sessionId: string, wires: SmartLinkWire[]): void {
    replace(sessionId, wires.map(parseWireLink))
  }

  /** Applies a link from the API or a pushed update. */
  function upsertLink(wire: SmartLinkWire): void {
    const link = parseWireLink(wire)
    const existing = bySession.value[link.sessionId] ?? []
    const index = existing.findIndex((l) => l.id === link.id)
    replace(
      link.sessionId,
      index >= 0 ? existing.map((l, i) => (i === index ? link : l)) : [...existing, link],
    )
  }

  function patchLink(sessionId: string, linkId: string, patch: Partial<SmartLink>): void {
    const existing = bySession.value[sessionId]
    if (!existing) return
    replace(sessionId, existing.map((l) => (l.id === linkId ? { ...l, ...patch } : l)))
  }

  /** Loads a session's links once; pushed updates keep them current afterwards. */
  function ensureLoaded(sessionId: string): Promise<void> {
    if (!sessionId || bySession.value[sessionId]) return Promise.resolve()
    const pending = loading.get(sessionId)
    if (pending) return pending

    const request = (async () => {
      try {
        const response = await apiFetch(path(sessionId, "/all"))
        if (response.ok) setLinks(sessionId, (await response.json()) as SmartLinkWire[])
      } catch {
        // Links are optional context; the session works without them.
      } finally {
        loading.delete(sessionId)
      }
    })()
    loading.set(sessionId, request)
    return request
  }

  function visibleLinks(sessionId: string): SmartLink[] {
    return (bySession.value[sessionId] ?? []).filter(isVisibleLink)
  }

  function headerLinks(sessionId: string): SmartLink[] {
    return visibleLinks(sessionId)
      .filter(isHeaderLink)
      .sort((a, b) => HEADER_ORDER[a.relationship] - HEADER_ORDER[b.relationship])
  }

  function lastCheckedAt(sessionId: string): string | null {
    return visibleLinks(sessionId)
      .map((l) => l.lastCheckedAt)
      .filter((value): value is string => !!value)
      .sort()
      .at(-1) ?? null
  }

  /** Attaches a pull request or issue URL. Returns an error message, or null on success. */
  async function addLink(sessionId: string, url: string): Promise<string | null> {
    try {
      const response = await apiFetch(path(sessionId), {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ url }),
      })
      if (response.status === 400) {
        return "Paste a link to a GitHub pull request or issue."
      }
      if (!response.ok) return `Couldn't attach the link (HTTP ${response.status}).`
      upsertLink((await response.json()) as SmartLinkWire)
      return null
    } catch {
      return "Couldn't reach Fleet to attach the link."
    }
  }

  async function setPinned(sessionId: string, linkId: string, pinned: boolean): Promise<void> {
    const link = bySession.value[sessionId]?.find((l) => l.id === linkId)
    if (!link) return
    const previous = link.relationship
    patchLink(sessionId, linkId, { relationship: pinned ? "pinned" : "mentioned" })
    try {
      const response = await apiFetch(path(sessionId, `/${encodeURIComponent(linkId)}/${pinned ? "pin" : "unpin"}`), { method: "PATCH" })
      if (!response.ok) patchLink(sessionId, linkId, { relationship: previous })
    } catch {
      patchLink(sessionId, linkId, { relationship: previous })
    }
  }

  async function dismiss(sessionId: string, linkId: string): Promise<void> {
    patchLink(sessionId, linkId, { isDismissed: true })
    try {
      const response = await apiFetch(path(sessionId, `/${encodeURIComponent(linkId)}/dismiss`), { method: "PATCH" })
      if (!response.ok) patchLink(sessionId, linkId, { isDismissed: false })
    } catch {
      patchLink(sessionId, linkId, { isDismissed: false })
    }
  }

  /** Asks the server to re-check the session's links now; results arrive as pushed updates. */
  async function refresh(sessionId: string): Promise<boolean> {
    try {
      const response = await apiFetch(path(sessionId, "/refresh"), { method: "POST" })
      return response.ok
    } catch {
      return false
    }
  }

  function requestFocus(sessionId: string, target: string): void {
    focusNonce += 1
    focusRequest.value = { sessionId, target, nonce: focusNonce }
  }

  return {
    bySession,
    focusRequest,
    setLinks,
    upsertLink,
    ensureLoaded,
    visibleLinks,
    headerLinks,
    lastCheckedAt,
    addLink,
    setPinned,
    dismiss,
    refresh,
    requestFocus,
  }
})
