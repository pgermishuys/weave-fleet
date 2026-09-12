<script setup lang="ts">
import { computed } from "vue";
import { useCanvasAnnotate } from "@/composables/use-canvas-annotation";
import { getVisualRenderer } from "@/lib/visual-renderer-registry";
import type { AnnotationAnchor } from "@/lib/annotation-types";
import type { VisualPayload } from "@/lib/visual-payload";
import { visualCanvasTitle } from "@/stores/canvases";

const props = defineProps<{
  payload: VisualPayload;
  /** Set on server canvases: the agent changes them, the user only looks. */
  readonly?: boolean;
}>();

const KIND_LABELS: Record<VisualPayload["$type"], string> = {
  "visual/flow": "Flow diagram",
  "visual/sequence": "Sequence diagram",
  markdown: "Document",
  html: "HTML",
};

const annotate = useCanvasAnnotate();
const title = computed(() => visualCanvasTitle(props.payload));
const kindLabel = computed(() => KIND_LABELS[props.payload.$type]);
const renderer = computed(() => getVisualRenderer(props.payload.$type));
const isMarkdown = computed(() => props.payload.$type === "markdown");
// Only the flow renderer takes readonly; the others render nothing editable.
const rendererProps = computed(() => (props.payload.$type === "visual/flow" ? { readonly: props.readonly } : {}));

function handleAnnotate(anchor: AnnotationAnchor, position: { x: number; y: number }): void {
  annotate(anchor, position, props.payload.sourceFilePath ?? title.value);
}
</script>

<template>
  <section class="visual-canvas">
    <div class="visual-canvas__bar">
      <span
        class="visual-canvas__title"
        :title="title"
      >{{ title }}</span>
      <span class="visual-canvas__kind">{{ kindLabel }}</span>
    </div>
    <div
      class="visual-canvas__content"
      :class="{ 'visual-canvas__content--document': isMarkdown }"
    >
      <component
        :is="renderer"
        v-if="renderer"
        :content="payload.content"
        :annotatable="isMarkdown"
        v-bind="rendererProps"
        @annotate="handleAnnotate"
      />
    </div>
  </section>
</template>

<style scoped>
.visual-canvas {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
}

.visual-canvas__bar {
  display: flex;
  align-items: center;
  gap: 8px;
  min-width: 0;
  height: 40px;
  flex-shrink: 0;
  padding: 0 14px;
  border-bottom: 1px solid var(--border);
  font-size: 12.5px;
}

.visual-canvas__title {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-weight: 600;
  color: var(--text);
}

.visual-canvas__kind {
  flex-shrink: 0;
  font-size: 12px;
  color: var(--muted);
}

.visual-canvas__content {
  flex: 1;
  min-height: 0;
  overflow: auto;
  background-image: radial-gradient(color-mix(in srgb, var(--text) 7%, transparent) 1px, transparent 1px);
  background-size: 16px 16px;
}

/* The canvas frames the diagram, so the renderer drops its own card chrome. */
.visual-canvas__content :deep(.flow-renderer),
.visual-canvas__content :deep(.mermaid-container) {
  border: 0;
  border-radius: 0;
}

.visual-canvas__content--document {
  padding: 12px 14px;
  background-image: none;
}
</style>
