import { defineStore } from "pinia"
import { shallowRef } from "vue"
import { apiFetch } from "@/lib/api-client"
import { isHeaderLink, isPullRequest, isVisibleLink, parseWireLink, type SmartLink, type SmartLinkWire } from "@/lib/smart-links"

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

// Which of a session's pull requests is "its" pull request: one it opened, then the one it started from, then
// one the user pinned; open ones before merged or closed ones.
const PULL_REQUEST_ORDER = { own: 0, origin: 1, pinned: 2, mentioned: 3 } as const

export const useSmartLinksStore = defineStore("smart-links", () => {
  const bySession = shallowRef<Record<string, SmartLink[]>>({})
  // Every session's header links (origin, own, pinned), from one request: the sessions list and GitHub pages
  // need them for sessions that were never opened.
  const headerBySession = shallowRef<Record<string, SmartLink[]>>({})
  const headerLinksLoaded = shallowRef(false)
  const focusRequest = shallowRef<SmartLinkFocusRequest | null>(null)
  const loading = new Map<string, Promise<void>>()
  let loadingAll: Promise<void> | null = null
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
    upsertHeaderLink(link)
  }

  function upsertHeaderLink(link: SmartLink): void {
    if (!headerLinksLoaded.value) return
    const others = (headerBySession.value[link.sessionId] ?? []).filter((l) => l.id !== link.id)
    const keep = isHeaderLink(link) && !link.isDismissed
    headerBySession.value = { ...headerBySession.value, [link.sessionId]: keep ? [...others, link] : others }
  }

  /**
   * Applies a pushed update: always to the header links, and to a session's full list once it's loaded
   * (sessions that were never opened load their full list on first view instead).
   */
  function applyPushed(wire: SmartLinkWire): void {
    if (bySession.value[wire.sessionId]) {
      upsertLink(wire)
    } else {
      upsertHeaderLink(parseWireLink(wire))
    }
  }

  /** Loads every session's header links once; pushed updates keep them current afterwards. */
  function ensureHeaderLinksLoaded(): Promise<void> {
    if (headerLinksLoaded.value) return Promise.resolve()
    if (loadingAll) return loadingAll

    loadingAll = (async () => {
      try {
        const response = await apiFetch("/api/smart-links")
        if (!response.ok) return
        const grouped: Record<string, SmartLink[]> = {}
        for (const link of ((await response.json()) as SmartLinkWire[]).map(parseWireLink)) {
          (grouped[link.sessionId] ??= []).push(link)
        }
        headerBySession.value = grouped
        headerLinksLoaded.value = true
      } catch {
        // Badges are extra; the list works without them.
      } finally {
        loadingAll = null
      }
    })()
    return loadingAll
  }

  /** Loads every session's header links again, e.g. after missing pushed updates while disconnected. */
  function reloadHeaderLinks(): Promise<void> {
    headerLinksLoaded.value = false
    return ensureHeaderLinksLoaded()
  }

  /** A session's header links: its full list when it's been opened, otherwise the ones loaded for every session. */
  function knownHeaderLinks(sessionId: string): SmartLink[] {
    const full = bySession.value[sessionId]
    return full ? full.filter((l) => isVisibleLink(l) && isHeaderLink(l)) : (headerBySession.value[sessionId] ?? [])
  }

  /** The pull request a session is about, for its row badge and header pill; null when it has none. */
  function sessionPullRequest(sessionId: string): SmartLink | null {
    return knownHeaderLinks(sessionId)
      .filter(isPullRequest)
      .sort((a, b) => Number(a.isTerminal) - Number(b.isTerminal)
        || PULL_REQUEST_ORDER[a.relationship] - PULL_REQUEST_ORDER[b.relationship])[0] ?? null
  }

  /**
   * The sessions working on a pull request or issue (`owner/repo#123`), for GitHub pages: the ones it's the
   * header link of. Needs `ensureHeaderLinksLoaded`.
   */
  function sessionsFor(resourceId: string): string[] {
    const wanted = resourceId.toLowerCase()
    const sessionIds = new Set<string>()
    for (const [sessionId, links] of Object.entries(headerBySession.value)) {
      if (links.some((l) => l.resourceId.toLowerCase() === wanted)) sessionIds.add(sessionId)
    }
    for (const [sessionId, links] of Object.entries(bySession.value)) {
      if (links.some((l) => isVisibleLink(l) && isHeaderLink(l) && l.resourceId.toLowerCase() === wanted)) sessionIds.add(sessionId)
      else sessionIds.delete(sessionId)
    }
    return [...sessionIds]
  }

  function patchLink(sessionId: string, linkId: string, patch: Partial<SmartLink>): void {
    const existing = bySession.value[sessionId]
    if (!existing) return
    replace(sessionId, existing.map((l) => (l.id === linkId ? { ...l, ...patch } : l)))
    const patched = bySession.value[sessionId]?.find((l) => l.id === linkId)
    if (patched) upsertHeaderLink(patched)
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
    headerBySession,
    headerLinksLoaded,
    focusRequest,
    setLinks,
    upsertLink,
    applyPushed,
    ensureLoaded,
    ensureHeaderLinksLoaded,
    reloadHeaderLinks,
    sessionPullRequest,
    sessionsFor,
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
