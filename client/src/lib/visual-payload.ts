export interface VisualPayload {
  $type: 'visual/sequence' | 'visual/flow' | 'html' | 'markdown'
  content: string | Record<string, unknown>
  title?: string
  sourceFilePath?: string
  sourceText?: string
  viewMode?: 'rendered' | 'source'
}

const VALID_TYPES = new Set(['visual/sequence', 'visual/flow', 'html', 'markdown'])

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
  try {
    const parsed = JSON.parse(raw)

    if (typeof parsed !== 'object' || parsed === null) {
      return null
    }

    const cleaned = stripDangerousKeys(parsed) as Record<string, unknown>

    if (typeof cleaned.$type !== 'string' || !VALID_TYPES.has(cleaned.$type)) {
      return null
    }

    if (cleaned.$type === 'visual/flow') {
      if (typeof cleaned.content !== 'string' && (typeof cleaned.content !== 'object' || cleaned.content === null || Array.isArray(cleaned.content))) {
        return null
      }
    } else {
      if (typeof cleaned.content !== 'string') {
        return null
      }
    }

    const payload: VisualPayload = {
      $type: cleaned.$type as VisualPayload['$type'],
      content: cleaned.$type === 'visual/flow' && typeof cleaned.content === 'object'
        ? cleaned.content as Record<string, unknown>
        : cleaned.content as string,
    }

    if (typeof cleaned.title === 'string') {
      payload.title = cleaned.title
    }

    return payload
  } catch {
    return null
  }
}
