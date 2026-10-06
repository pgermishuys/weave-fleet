import { sanitizeHtml } from '@/lib/sanitize-html'

type Mermaid = typeof import('mermaid').default

let loading: Promise<Mermaid> | null = null
let renderCounter = 0

// Mermaid is large, so it loads with the first diagram rather than with the page.
function loadMermaid(): Promise<Mermaid> {
  loading ??= import('mermaid').then(({ default: mermaid }) => {
    mermaid.initialize({
      startOnLoad: false,
      securityLevel: 'strict',
      htmlLabels: false,
      theme: 'default',
      // Otherwise a source that doesn't parse leaves Mermaid's "Syntax error" drawing at the end of <body>.
      suppressErrorRendering: true,
    })
    return mermaid
  })
  return loading
}

/**
 * Agents often escape `<` and `>` in labels as `&lt;` and `&gt;`. Mermaid reads the `;` as the end of a
 * statement, so the diagram doesn't parse. Mermaid's own entity codes (`#lt;`) mean the same thing and do.
 */
export function mermaidEntityCodes(source: string): string {
  return source.replace(/&#?(\w+);/g, '#$1;')
}

/** Renders Mermaid source to sanitized SVG markup. Throws when the source doesn't parse. */
export async function renderMermaidSvg(source: string): Promise<string> {
  const mermaid = await loadMermaid()
  const { svg } = await mermaid.render(`mermaid-${Date.now()}-${++renderCounter}`, mermaidEntityCodes(source))
  return sanitizeHtml(svg)
}
