import { ref, watch, type Ref } from 'vue'
import { browseSessionDirectory, readSessionFile } from '@/api/session-files'
import type { BrowseDirectoryEntry } from '@/api/client'
import { buildPayloadForFile } from '@/lib/file-payload'
import { useVisualPanel } from '@/composables/use-visual-panel'

export function useFileBrowser(sessionId: Ref<string | null>) {
  const { showVisual } = useVisualPanel()

  // State
  const rootEntries = ref<BrowseDirectoryEntry[]>([])
  const expandedDirs = ref<Map<string, BrowseDirectoryEntry[]>>(new Map())
  const loadingDirs = ref<Set<string>>(new Set())
  const rootLoading = ref(false)
  const error = ref<string | null>(null)

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

  async function selectFile(path: string): Promise<void> {
    if (!sessionId.value) {
      error.value = 'No session ID provided'
      return
    }

    error.value = null

    try {
      const response = await readSessionFile(sessionId.value, path)
      
      if (response.isBinary) {
        // Show a visual payload with a binary file message
        const binaryPayload = {
          $type: 'markdown' as const,
          content: `# Binary File\n\nCannot display binary file: \`${path}\`\n\nThis file is a binary file and cannot be previewed as text.`,
          sourceFilePath: path,
          sourceText: '',
          viewMode: 'rendered' as const,
        }
        showVisual(binaryPayload)
        return
      }

      const payload = buildPayloadForFile(path, response.content || '')
      showVisual(payload)
    } catch (err) {
      error.value = err instanceof Error ? err.message : `Failed to read file: ${path}`
      console.error('[useFileBrowser] selectFile error:', err)
    }
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
