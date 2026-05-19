import { onUnmounted, ref, watch, type Ref } from 'vue'
import type { AccumulatedMessage } from '@/lib/api-types'
import { apiFetch } from '@/lib/api-client'
import { useSmartLinksStore } from '@/stores/smart-links'
import { extractUrls } from '../utils/extract-urls'
import { useSmartLinkProviders } from './use-smart-link-providers'
import type { SmartLinkResolution } from '../types'

export const POLL_INTERVAL_SECONDS = 30

// Module-level reactive state shared across all consumers
export const secondsUntilRefresh = ref(POLL_INTERVAL_SECONDS)
export const isRefreshing = ref(false)

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
  originUrl?: Ref<string | null>
}

// Module-level countdown timer — one instance drives all consumers
let countdownTimer: ReturnType<typeof setInterval> | undefined
let activeSessionId: string | null = null
let consumerCount = 0

function startCountdown(): void {
  if (countdownTimer !== undefined) return
  countdownTimer = setInterval(() => {
    secondsUntilRefresh.value -= 1
    if (secondsUntilRefresh.value <= 0) {
      secondsUntilRefresh.value = POLL_INTERVAL_SECONDS
      if (activeSessionId) {
        void runRefresh(activeSessionId)
      }
    }
  }, 1000)
}

function stopCountdown(): void {
  if (countdownTimer !== undefined) {
    clearInterval(countdownTimer)
    countdownTimer = undefined
  }
}

async function runRefresh(sid: string): Promise<void> {
  if (isRefreshing.value) return
  isRefreshing.value = true
  try {
    const store = useSmartLinksStore()
    const links = store.getAllLinks(sid).filter((l) => !l.isDismissed && !l.isTerminal)
    for (const link of links) {
      await refreshSingleLink(sid, link.url)
    }
  } finally {
    isRefreshing.value = false
  }
}

export async function refreshNow(): Promise<void> {
  if (!activeSessionId) return
  secondsUntilRefresh.value = POLL_INTERVAL_SECONDS
  await runRefresh(activeSessionId)
}

export function useSmartLinks(options: UseSmartLinksOptions): void {
  const { sessionId, messages, originUrl } = options
  const store = useSmartLinksStore()
  const providers = useSmartLinkProviders()

  let disposed = false
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

  /** Detect new URLs from messages (and origin) and resolve them via providers */
  async function detectAndResolveUrls(sid: string): Promise<void> {
    const text = extractAllText()
    const urls = extractUrls(text)

    // Include the session origin URL if present
    const origin = originUrl?.value?.trim()
    if (origin && !urls.includes(origin)) {
      urls.unshift(origin)
    }

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

  // Wire up module-level consumer tracking
  consumerCount++

  async function initialize(sid: string): Promise<void> {
    await loadLinks(sid)
    if (!disposed) await detectAndResolveUrls(sid)
  }

  // Watch for session changes — start countdown when session is active
  const stopWatch = watch(
    sessionId,
    async (sid) => {
      if (!sid) return
      activeSessionId = sid
      secondsUntilRefresh.value = POLL_INTERVAL_SECONDS
      await initialize(sid)
      if (disposed) return
      startCountdown()
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
    consumerCount--
    if (consumerCount <= 0) {
      stopCountdown()
      activeSessionId = null
      consumerCount = 0
    }
    stopWatch()
    stopMessagesWatch()
  })
}
