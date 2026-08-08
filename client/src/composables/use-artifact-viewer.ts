import { computed, ref, watch } from 'vue'
import { useVisualPanel } from '@/composables/use-visual-panel'
import { useSessionDiffsContext } from '@/composables/use-session-diffs-context'
import type { VisualPayload } from '@/lib/visual-payload'
import { getFileExtension, isRenderableExtension, buildPayloadForFile } from '@/lib/file-payload'

export function useArtifactViewer() {
  const { visualPayload, showVisual, clearVisual } = useVisualPanel()
  const diffsContext = useSessionDiffsContext()

  const activeFilePath = ref<string | null>(null)
  const viewMode = ref<'rendered' | 'source'>('rendered')
  const originalPayload = ref<VisualPayload | null>(null)

  const availableFiles = computed(() => {
    const diffs = diffsContext.value?.diffState.diffs.value
    if (!diffs) return []

    return diffs
      .filter((diff) => {
        const ext = getFileExtension(diff.file)
        return isRenderableExtension(ext)
      })
      .map((diff) => diff.file)
  })

  function buildSourcePayload(originalPayload: VisualPayload): VisualPayload {
    const ext = getFileExtension(originalPayload.sourceFilePath || '')
    const lang = ext.slice(1) || 'text'
    const fencedContent = `\`\`\`${lang}\n${originalPayload.sourceText}\n\`\`\``

    return {
      $type: 'markdown',
      content: fencedContent,
      sourceFilePath: originalPayload.sourceFilePath,
      sourceText: originalPayload.sourceText,
      viewMode: 'source',
    }
  }

  function openFile(path: string): void {
    const diffs = diffsContext.value?.diffState.diffs.value
    if (!diffs) {
      console.warn('[useArtifactViewer] No diffs available')
      return
    }

    const diff = diffs.find((d) => d.file === path)
    if (!diff) {
      console.warn(`[useArtifactViewer] File not found in diffs: ${path}`)
      return
    }

    const content = diff.after || ''
    const payload = buildPayloadForFile(path, content)

    activeFilePath.value = path
    originalPayload.value = payload
    viewMode.value = payload.viewMode || 'rendered'

    showVisual(payload)
  }

  function closeViewer(): void {
    clearVisual()
    activeFilePath.value = null
    originalPayload.value = null
    viewMode.value = 'rendered'
  }

  // Watch viewMode and toggle payload accordingly
  watch(viewMode, (newMode) => {
    if (!originalPayload.value) return

    if (newMode === 'source') {
      const sourcePayload = buildSourcePayload(originalPayload.value)
      showVisual(sourcePayload)
    } else {
      // Restore original rendered payload
      showVisual(originalPayload.value)
    }
  })

  return {
    visualPayload,
    activeFilePath,
    viewMode,
    availableFiles,
    openFile,
    closeViewer,
  }
}
