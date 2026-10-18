export interface VisualPayload {
  $type: 'html' | 'markdown'
  content: string
  title?: string
  sourceFilePath?: string
  sourceText?: string
  viewMode?: 'rendered' | 'source'
}

const VALID_TYPES = new Set(['html', 'markdown'])
const JSON_OBJECT_START = /^\s*\{/

/**
 * Recursively strips dangerous prototype pollution keys from an object.
 */
function stripDangerousKeys(obj: unknown): unknown {
  if (obj === null || typeof obj !== 'object') {
    return obj
  }

  if (Array.isArray(obj)) {
    return obj.map(stripDangerousKeys)
  }

  const cleaned: Record<string, unknown> = {}
  for (const [key, value] of Object.entries(obj)) {
    if (key === '__proto__' || key === 'constructor') {
      continue
    }
    cleaned[key] = stripDangerousKeys(value)
  }

  return cleaned
}

/**
 * Parses a raw string as JSON and validates it as a VisualPayload.
 * Returns null if the string is not valid JSON, does not have a recognized $type,
 * or is missing required fields.
 * Strips __proto__ and constructor keys recursively before returning.
 */
export function parseVisualPayload(raw: string): VisualPayload | null {
  // Most tool output (file reads, command output) isn't JSON, and a failed JSON.parse throws, which is slow.
  if (!JSON_OBJECT_START.test(raw)) {
    return null
  }

  try {
    const parsed = JSON.parse(raw)

    if (typeof parsed !== 'object' || parsed === null) {
      return null
    }

    const cleaned = stripDangerousKeys(parsed) as Record<string, unknown>

    if (typeof cleaned.$type !== 'string' || !VALID_TYPES.has(cleaned.$type)) {
      return null
    }

    if (typeof cleaned.content !== 'string') {
      return null
    }

    const payload: VisualPayload = {
      $type: cleaned.$type as VisualPayload['$type'],
      content: cleaned.content,
    }

    if (typeof cleaned.title === 'string') {
      payload.title = cleaned.title
    }

    return payload
  } catch {
    return null
  }
}
