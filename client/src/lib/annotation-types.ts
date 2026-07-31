/**
 * Annotation anchor types for capturing UI element or text range references.
 */

/**
 * Anchor that references a DOM element via CSS selector path.
 */
export interface ElementAnchor {
  type: 'element'
  selectorPath: string
  elementText: string
}

/**
 * Anchor that references a text range with start/end offsets.
 */
export interface TextRangeAnchor {
  type: 'text-range'
  selectedText: string
  startOffset: number
  endOffset: number
}

/**
 * Discriminated union of all anchor types.
 */
export type AnnotationAnchor = ElementAnchor | TextRangeAnchor

/**
 * Extracts the text content from an anchor, truncating to 240 characters with ellipsis.
 */
export function extractAnchorText(anchor: AnnotationAnchor): string {
  const text = anchor.type === 'element' ? anchor.elementText : anchor.selectedText
  return text.length > 240 ? text.slice(0, 240) + '...' : text
}
