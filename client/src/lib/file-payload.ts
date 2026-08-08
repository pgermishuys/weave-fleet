import type { VisualPayload } from '@/lib/visual-payload'

const RENDERABLE_EXTENSIONS = ['.md', '.html']

/**
 * Extract file extension from a path.
 */
export function getFileExtension(path: string): string {
  const lastDot = path.lastIndexOf('.')
  return lastDot === -1 ? '' : path.slice(lastDot)
}

/**
 * Check if a file extension is renderable (markdown or HTML).
 */
export function isRenderableExtension(ext: string): boolean {
  return RENDERABLE_EXTENSIONS.includes(ext)
}

/**
 * Build a VisualPayload from a file path and content.
 * - .md files → markdown payload
 * - .html files → html payload
 * - Other files → markdown payload with fenced code block
 */
export function buildPayloadForFile(path: string, content: string): VisualPayload {
  const ext = getFileExtension(path)

  if (ext === '.md') {
    return {
      $type: 'markdown',
      content,
      sourceFilePath: path,
      sourceText: content,
      viewMode: 'rendered',
    }
  }

  if (ext === '.html') {
    return {
      $type: 'html',
      content,
      sourceFilePath: path,
      sourceText: content,
      viewMode: 'rendered',
    }
  }

  // Fallback: wrap in fenced code block
  const lang = ext.slice(1) // remove leading dot
  const fencedContent = `\`\`\`${lang}\n${content}\n\`\`\``
  return {
    $type: 'markdown',
    content: fencedContent,
    sourceFilePath: path,
    sourceText: content,
    viewMode: 'source',
  }
}
