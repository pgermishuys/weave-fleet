import { onUnmounted, watch, type Ref } from 'vue'
import type { AccumulatedMessage } from '@/lib/api-types'
import { apiFetch } from '@/lib/api-client'
import { useSmartLinksStore } from '@/stores/smart-links'
import { extractUrls } from '../utils/extract-urls'
import { useSmartLinkProviders } from './use-smart-link-providers'
import type { SmartLinkResolution } from '../types'

const POLL_INTERVAL_MS = 30_000

/** Build the POST payload for upserting a smart link */
function buildSmartLinkPayload(url: string, resolution: SmartLinkResolution) {
  return {
    url,
    providerId: resolution.providerId,
    resourceType: resolution.resourceType,
    resourceId: resolution.resourceId,
    title: resolution.title,
    status: resolution.status,
    statusLabel: resolution.statusLabel,
    metadataJson: resolution.metadata ? JSON.stringify(resolution.metadata) : null,
    isTerminal: resolution.isTerminal,
  }
}

/** Refresh a single link by URL — resolves via provider and persists to backend. Silently ignores errors. */
export async function refreshSingleLink(sessionId: string, url: string): Promise<void> {
  const providers = useSmartLinkProviders()
  const store = useSmartLinksStore()
  const provider = providers.findProvider(url)
  if (!provider) return
  try {
    const resolution = await provider.resolve(url)
    if (!resolution) return
    const response = await apiFetch(`/api/sessions/${sessionId}/smart-links`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(buildSmartLinkPayload(url, resolution)),
    })
    if (response.ok) {
      const updated = await response.json()
      store.upsertLink(updated)
    }
  } catch {
    // silently ignore
  }
}

export interface UseSmartLinksOptions {
  sessionId: Ref<string | null>
  messages: Ref<readonly AccumulatedMessage[]>
}

export function useSmartLinks(options: UseSmartLinksOptions): void {
  const { sessionId, messages } = options
  const store = useSmartLinksStore()
  const providers = useSmartLinkProviders()

  let disposed = false
  let pollTimer: ReturnType<typeof setInterval> | undefined
  let requestId = 0

  /** Extract text content from all messages */
  function extractAllText(): string {
    return messages.value
      .flatMap((m) =>
        m.parts
          .filter((p): p is { partId: string; type: 'text'; text: string } => p.type === 'text')
          .map((p) => p.text),
      )
      .join('\n')
  }

  /** Load all smart links (including dismissed) for dedup tracking */
  async function loadLinks(sid: string): Promise<void> {
    try {
      const response = await apiFetch(`/api/sessions/${sid}/smart-links/all`)
      if (!response.ok || disposed) return
      const links = await response.json()
      if (!disposed) store.setLinks(sid, links)
    } catch {
      // silently ignore — will retry on next poll
    }
  }

  /** Detect new URLs from messages and resolve them via providers */
  async function detectAndResolveUrls(sid: string): Promise<void> {
    const text = extractAllText()
    const urls = extractUrls(text)

    for (const url of urls) {
      if (disposed) return
      // Skip already-known (upsert handles idempotency, but avoid API round-trips)
      if (store.isUrlKnown(sid, url)) continue

      const provider = providers.findProvider(url)
      if (!provider) continue

      try {
        const resolution = await provider.resolve(url)
        if (!resolution || disposed) continue

        const currentRequestId = ++requestId
        const response = await apiFetch(`/api/sessions/${sid}/smart-links`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(buildSmartLinkPayload(url, resolution)),
        })
        if (response.ok && !disposed && currentRequestId === requestId) {
          const link = await response.json()
          store.upsertLink(link)
        }
      } catch {
        // silently ignore
      }
    }
  }

  /** Refresh status of non-terminal, non-dismissed links */
  async function refreshLinkStatuses(sid: string): Promise<void> {
    const links = store.getAllLinks(sid).filter((l) => !l.isDismissed && !l.isTerminal)
    for (const link of links) {
      if (disposed) return
      await refreshSingleLink(sid, link.url)
    }
  }

  async function initialize(sid: string): Promise<void> {
    await loadLinks(sid)
    if (!disposed) await detectAndResolveUrls(sid)
  }

  // Watch for session changes
  const stopWatch = watch(
    sessionId,
    async (sid) => {
      if (!sid) return
      clearInterval(pollTimer)
      await initialize(sid)
      if (disposed) return

      pollTimer = setInterval(async () => {
        if (!sessionId.value || disposed) return
        await refreshLinkStatuses(sessionId.value)
        await detectAndResolveUrls(sessionId.value)
      }, POLL_INTERVAL_MS)
    },
    { immediate: true },
  )

  // Re-scan on new messages
  const stopMessagesWatch = watch(messages, async () => {
    if (!sessionId.value || disposed) return
    await detectAndResolveUrls(sessionId.value)
  })

  onUnmounted(() => {
    disposed = true
    clearInterval(pollTimer)
    stopWatch()
    stopMessagesWatch()
  })
}
