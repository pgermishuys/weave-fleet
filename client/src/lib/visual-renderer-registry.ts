import type { Component } from 'vue'
import { defineAsyncComponent } from 'vue'
import HtmlRenderer from '@/components/visual-renderers/HtmlRenderer.vue'
import MarkdownRenderer from '@/components/visual-renderers/MarkdownRenderer.vue'
import VueFlowRenderer from '@/components/visual-renderers/VueFlowRenderer.vue'

const MermaidRenderer = defineAsyncComponent(() => import('@/components/visual-renderers/MermaidRenderer.vue'))

const registry: Record<string, Component> = {
  'visual/sequence': MermaidRenderer,
  'visual/flow': VueFlowRenderer,
  'html': HtmlRenderer,
  'markdown': MarkdownRenderer,
}

export function getVisualRenderer(type: string): Component | null {
  return registry[type] ?? null
}
