import type { Component } from 'vue'
import { defineContributionPoint } from '@/lib/contributions'
import HtmlRenderer from '@/components/visual-renderers/HtmlRenderer.vue'
import MarkdownRenderer from '@/components/visual-renderers/MarkdownRenderer.vue'

export interface VisualRenderer {
  /** The payload `$type` this renders. */
  type: string
  component: Component
}

/** What draws each kind of visual payload (HTML or Markdown). */
export const visualRenderers = defineContributionPoint<VisualRenderer>({
  name: 'visual renderers',
  idOf: (renderer) => renderer.type,
})

visualRenderers.contribute('core', [
  { type: 'html', component: HtmlRenderer },
  { type: 'markdown', component: MarkdownRenderer },
])

export function getVisualRenderer(type: string): Component | null {
  return visualRenderers.get(type)?.component ?? null
}
