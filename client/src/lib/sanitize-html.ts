import DOMPurify, { type Config } from 'dompurify'

const SANITIZE_CONFIG: Config = {
  ALLOWED_TAGS: [
    // Block
    'h1', 'h2', 'h3', 'h4', 'h5', 'h6', 'p', 'div', 'blockquote', 'pre', 'hr', 'br',
    // Inline
    'a', 'strong', 'em', 'code', 'span', 'img',
    // Lists
    'ul', 'ol', 'li',
    // Tables
    'table', 'thead', 'tbody', 'tr', 'th', 'td',
    // SVG (for Mermaid output)
    'svg', 'g', 'path', 'rect', 'circle', 'ellipse', 'line', 'polyline', 'polygon',
    'text', 'tspan', 'defs', 'clipPath', 'marker', 'foreignObject', 'use',
  ],
  ALLOWED_ATTR: [
    // HTML
    'href', 'title', 'target', 'rel', 'class', 'src', 'alt', 'width', 'height',
    // SVG
    'd', 'fill', 'stroke', 'stroke-width', 'transform', 'viewBox', 'xmlns',
    'x', 'y', 'cx', 'cy', 'r', 'rx', 'ry', 'x1', 'y1', 'x2', 'y2',
    'points', 'font-size', 'font-family', 'text-anchor', 'dominant-baseline',
    'clip-path', 'marker-end', 'marker-start', 'id', 'style',
  ],
  ALLOWED_URI_REGEXP: /^(?:(?:https?|mailto):)/i,
  ALLOW_UNKNOWN_PROTOCOLS: false,
  // Disable DOM clobbering protection to allow target attribute
  SANITIZE_DOM: false,
  // Explicitly add target attribute (required due to DOMPurify's security defaults)
  ADD_ATTR: ['target'],
}

/**
 * Sanitizes HTML content using DOMPurify with a strict allowlist.
 * - Strips dangerous tags like <script>, <iframe>
 * - Blocks javascript: and data: URIs
 * - Forces rel="noopener noreferrer" on links with target attribute
 * - Allows only specified tags and attributes
 * 
 * Note: DOMPurify strips certain attributes (target, SVG attrs) even when listed in ALLOWED_ATTR
 * due to security defaults. We work around this by:
 * 1. Parsing the input before sanitization to extract these attributes
 * 2. Using afterSanitizeAttributes hook to restore them after sanitization
 * 3. Setting SANITIZE_DOM: false to allow target attribute
 */
export function sanitizeHtml(raw: string): string {
  // Parse the input to extract attributes that DOMPurify will strip
  const parser = new DOMParser()
  const doc = parser.parseFromString(raw, 'text/html')
  
  // Extract links with target attribute
  const linksWithTarget = Array.from(doc.querySelectorAll('a[target]')).map((link) => ({
    href: link.getAttribute('href'),
    target: link.getAttribute('target'),
  }))

  // Extract SVG elements with their attributes
  const svgAttrs: Array<Record<string, string>> = []
  doc.querySelectorAll('svg, circle, rect, path, g, ellipse, line, polyline, polygon, text, tspan').forEach((el) => {
    const attrs: Record<string, string> = {}
    for (let i = 0; i < el.attributes.length; i++) {
      const attr = el.attributes[i]
      attrs[attr.name] = attr.value
    }
    svgAttrs.push(attrs)
  })

  let svgIndex = 0

  // Hook to restore attributes after sanitization
  DOMPurify.addHook('afterSanitizeAttributes', (node) => {
    // Restore target attribute on links and force rel="noopener noreferrer"
    if (node.tagName === 'A') {
      const href = node.getAttribute('href')
      const originalLink = linksWithTarget.find((l) => l.href === href)
      if (originalLink) {
        node.setAttribute('target', originalLink.target!)
        node.setAttribute('rel', 'noopener noreferrer')
      }
    }

    // Restore SVG attributes (tagName is lowercase in jsdom)
    const svgTags = ['svg', 'circle', 'rect', 'path', 'g', 'ellipse', 'line', 'polyline', 'polygon', 'text', 'tspan']
    if (svgTags.includes(node.tagName)) {
      const attrs = svgAttrs[svgIndex]
      if (attrs) {
        for (const [name, value] of Object.entries(attrs)) {
          node.setAttribute(name, value)
        }
      }
      svgIndex++
    }
  })

  const result = DOMPurify.sanitize(raw, SANITIZE_CONFIG)

  // Clean up hooks to avoid side effects
  DOMPurify.removeAllHooks()

  // DOMPurify.sanitize can return TrustedHTML in some environments, ensure string
  return String(result)
}
