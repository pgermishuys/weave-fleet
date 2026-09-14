import { ref, watch, type Ref } from 'vue'
import { browseSessionDirectory } from '@/api/session-files'
import type { BrowseDirectoryEntry } from '@/api/client'
import { useWeaveSocket } from '@/composables/use-weave-socket'
import type { DomainEvent } from '@/lib/domain-events'
import { useCanvasesStore } from '@/stores/canvases'

export function useFileBrowser(sessionId: Ref<string | null>) {
  const { subscribeV2 } = useWeaveSocket()
  const canvases = useCanvasesStore()

  // State
  const rootEntries = ref<BrowseDirectoryEntry[]>([])
  const expandedDirs = ref<Map<string, BrowseDirectoryEntry[]>>(new Map())
  const loadingDirs = ref<Set<string>>(new Set())
  const rootLoading = ref(false)
  const error = ref<string | null>(null)

  // Debounce state for file change events
  let debounceTimeoutId: ReturnType<typeof setTimeout> | undefined

  // Actions
  async function loadRoot(): Promise<void> {
    if (!sessionId.value) {
      error.value = 'No session ID provided'
      return
    }

    rootLoading.value = true
    error.value = null

    try {
      const response = await browseSessionDirectory(sessionId.value)
      rootEntries.value = response.entries || []
    } catch (err) {
      error.value = err instanceof Error ? err.message : 'Failed to load root directory'
      console.error('[useFileBrowser] loadRoot error:', err)
    } finally {
      rootLoading.value = false
    }
  }

  async function expandDirectory(path: string): Promise<void> {
    if (!sessionId.value) {
      error.value = 'No session ID provided'
      return
    }

    // If already expanded, do nothing (cached)
    if (expandedDirs.value.has(path)) {
      return
    }

    loadingDirs.value.add(path)
    error.value = null

    try {
      const response = await browseSessionDirectory(sessionId.value, path)
      expandedDirs.value.set(path, response.entries || [])
    } catch (err) {
      error.value = err instanceof Error ? err.message : `Failed to load directory: ${path}`
      console.error('[useFileBrowser] expandDirectory error:', err)
    } finally {
      loadingDirs.value.delete(path)
    }
  }

  function collapseDirectory(path: string): void {
    // Remove from expanded map (keeps cache, but hides from UI perspective)
    // If you want to clear cache, uncomment the next line:
    // expandedDirs.value.delete(path)
    
    // For now, we'll just remove it to force refetch on next expand
    expandedDirs.value.delete(path)
  }

  function isExpanded(path: string): boolean {
    return expandedDirs.value.has(path)
  }

  function isLoading(path: string): boolean {
    return loadingDirs.value.has(path)
  }

  /** Open a file in its own tab: a preview tab, or a kept one (double-click, Enter in search). */
  function selectFile(path: string, options: { keep?: boolean } = {}): void {
    if (!sessionId.value) {
      error.value = 'No session ID provided'
      return
    }

    error.value = null
    canvases.openFile(sessionId.value, path, { keep: options.keep })
  }

  async function refresh(): Promise<void> {
    // Capture currently expanded directories
    const expandedPaths = Array.from(expandedDirs.value.keys())

    // Clear all state
    rootEntries.value = []
    expandedDirs.value.clear()
    loadingDirs.value.clear()
    error.value = null

    // Reload root
    await loadRoot()

    // Re-expand previously expanded directories
    for (const path of expandedPaths) {
      await expandDirectory(path)
    }
  }

  // Watch sessionId and reload when it changes
  watch(
    sessionId,
    (newId, oldId) => {
      if (newId !== oldId) {
        // Clear debounce timeout on session change
        if (debounceTimeoutId !== undefined) {
          clearTimeout(debounceTimeoutId)
          debounceTimeoutId = undefined
        }

        // Clear state
        rootEntries.value = []
        expandedDirs.value.clear()
        loadingDirs.value.clear()
        error.value = null

        // Load new session root if ID is present
        if (newId) {
          loadRoot()
        }
      }
    },
    { immediate: true }
  )

  // Subscribe to files.changed events
  watch(
    sessionId,
    (activeSessionId, _previousSessionId, onCleanup) => {
      if (!activeSessionId) {
        return
      }

      const unsubscribe = subscribeV2(
        `session:${activeSessionId}`,
        () => {
          // File browser state is loaded from the REST endpoint; snapshots are ignored here.
        },
        (event: DomainEvent) => {
          if (event.type !== 'files.changed' || event.payload.sessionId !== activeSessionId) {
            return
          }

          // Debounce loadRoot to avoid flooding the API during rapid edits
          if (debounceTimeoutId !== undefined) {
            clearTimeout(debounceTimeoutId)
          }

          debounceTimeoutId = setTimeout(() => {
            void loadRoot()
          }, 500)
        }
      )

      onCleanup(() => {
        unsubscribe()
        if (debounceTimeoutId !== undefined) {
          clearTimeout(debounceTimeoutId)
          debounceTimeoutId = undefined
        }
      })
    },
    { immediate: true }
  )

  return {
    rootEntries,
    expandedDirs,
    loadingDirs,
    rootLoading,
    error,
    loadRoot,
    expandDirectory,
    collapseDirectory,
    isExpanded,
    isLoading,
    selectFile,
    refresh,
  }
}
