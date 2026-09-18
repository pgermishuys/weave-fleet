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
    })
    return mermaid
  })
  return loading
}

/** Renders Mermaid source to sanitized SVG markup. Throws when the source doesn't parse. */
export async function renderMermaidSvg(source: string): Promise<string> {
  const mermaid = await loadMermaid()
  const { svg } = await mermaid.render(`mermaid-${Date.now()}-${++renderCounter}`, source)
  return sanitizeHtml(svg)
}
