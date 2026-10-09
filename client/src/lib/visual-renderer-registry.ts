import type { Component } from 'vue'
import { defineAsyncComponent } from 'vue'
import { defineContributionPoint } from '@/lib/contributions'
import HtmlRenderer from '@/components/visual-renderers/HtmlRenderer.vue'
import MarkdownRenderer from '@/components/visual-renderers/MarkdownRenderer.vue'
import VueFlowRenderer from '@/components/visual-renderers/VueFlowRenderer.vue'

const MermaidRenderer = defineAsyncComponent(() => import('@/components/visual-renderers/MermaidRenderer.vue'))

export interface VisualRenderer {
  /** The payload `$type` this renders. */
  type: string
  component: Component
}

/** What draws each kind of visual payload (a diagram, a flow, Markdown, a page). */
export const visualRenderers = defineContributionPoint<VisualRenderer>({
  name: 'visual renderers',
  idOf: (renderer) => renderer.type,
})

visualRenderers.contribute('core', [
  { type: 'visual/sequence', component: MermaidRenderer },
  { type: 'visual/flow', component: VueFlowRenderer },
  { type: 'html', component: HtmlRenderer },
  { type: 'markdown', component: MarkdownRenderer },
])

export function getVisualRenderer(type: string): Component | null {
  return visualRenderers.get(type)?.component ?? null
}
