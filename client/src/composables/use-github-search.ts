import { onScopeDispose, shallowRef, toValue, watch, type MaybeRefOrGetter } from "vue"
import { searchGitHubItems, type GitHubItemSummary } from "@/lib/github-items"

const DEBOUNCE_MS = 250

/**
 * Pull requests or issues from GitHub search, a page at a time. A new query replaces the list after a short
 * pause, so typing in a filter doesn't send a request per key; null asks for nothing.
 */
export function useGitHubSearch(query: MaybeRefOrGetter<string | null>) {
  const items = shallowRef<GitHubItemSummary[]>([])
  const totalCount = shallowRef(0)
  const isLoading = shallowRef(false)
  const isLoadingMore = shallowRef(false)
  const error = shallowRef<string | null>(null)
  const hasMore = shallowRef(false)
  let cursor: string | null = null
  let controller: AbortController | null = null
  let timer: ReturnType<typeof setTimeout> | undefined

  async function load(q: string, append: boolean): Promise<void> {
    controller?.abort()
    const current = new AbortController()
    controller = current
    if (append) isLoadingMore.value = true
    else isLoading.value = true
    error.value = null
    try {
      const page = await searchGitHubItems(q, { after: append ? cursor : null, signal: current.signal })
      if (current.signal.aborted) return
      items.value = append ? [...items.value, ...page.items] : page.items
      totalCount.value = page.totalCount
      hasMore.value = page.hasNextPage
      cursor = page.endCursor
    } catch (loadError) {
      if (current.signal.aborted) return
      error.value = loadError instanceof Error ? loadError.message : "GitHub search failed."
      if (!append) items.value = []
    } finally {
      if (controller === current) {
        isLoading.value = false
        isLoadingMore.value = false
      }
    }
  }

  watch(
    () => toValue(query),
    (q, previous) => {
      clearTimeout(timer)
      if (!q) {
        controller?.abort()
        items.value = []
        totalCount.value = 0
        hasMore.value = false
        isLoading.value = false
        return
      }
      // The first search goes straight away; later ones wait for typing to pause.
      if (previous === undefined) void load(q, false)
      else {
        isLoading.value = true
        timer = setTimeout(() => void load(q, false), DEBOUNCE_MS)
      }
    },
    { immediate: true },
  )

  onScopeDispose(() => {
    clearTimeout(timer)
    controller?.abort()
  })

  function refresh(): Promise<void> {
    const q = toValue(query)
    return q ? load(q, false) : Promise.resolve()
  }

  function loadMore(): Promise<void> {
    const q = toValue(query)
    return q && hasMore.value ? load(q, true) : Promise.resolve()
  }

  return { items, totalCount, isLoading, isLoadingMore, error, hasMore, refresh, loadMore }
}
