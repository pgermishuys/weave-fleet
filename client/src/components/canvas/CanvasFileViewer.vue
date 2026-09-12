<script setup lang="ts">
import { computed, inject } from "vue";
import { FileText, GitCompare, X } from "lucide-vue-next";
import DiffView from "@/components/session/DiffView.vue";
import { useContentPanelContext } from "@/composables/use-content-panel";
import { useCanvasAnnotate } from "@/composables/use-canvas-annotation";
import type { UseDiffsResult } from "@/composables/use-diffs";
import { getVisualRenderer } from "@/lib/visual-renderer-registry";
import { parseDiffLines } from "@/lib/diff-parser";
import type { AnnotationAnchor } from "@/lib/annotation-types";

const contentPanel = useContentPanelContext();
const sharedDiffs = inject<UseDiffsResult>("sharedDiffs");
const annotate = useCanvasAnnotate();

const selectedFilePath = computed(() => contentPanel.filesContext.value.selectedFilePath);
// Ignore content still on screen from the previous selection while the new file loads.
const payload = computed(() => {
  const loaded = contentPanel.filePayload.value;
  return loaded && loaded.sourceFilePath === selectedFilePath.value ? loaded : null;
});
const renderer = computed(() => (payload.value ? getVisualRenderer(payload.value.$type) : null));
const isMarkdown = computed(() => payload.value?.$type === "markdown");

const selectedDiff = computed(() => {
  const path = selectedFilePath.value;
  if (!path) return null;
  return sharedDiffs?.diffs.value.find((diff) => diff.file === path) ?? null;
});

const diffLines = computed(() =>
  selectedDiff.value ? parseDiffLines(selectedDiff.value.before, selectedDiff.value.after) : null,
);

const hasDiff = computed(() => diffLines.value !== null);
const showDiff = computed(() => contentPanel.viewMode.value === "diff" && hasDiff.value);

function setFileView(): void {
  contentPanel.setViewMode("file");
}

function setDiffView(): void {
  if (hasDiff.value) contentPanel.setViewMode("diff");
}

function handleAnnotate(anchor: AnnotationAnchor, position: { x: number; y: number }): void {
  annotate(anchor, position, payload.value?.sourceFilePath ?? selectedFilePath.value ?? "");
}
</script>

<template>
  <section class="canvas-file-viewer">
    <div class="canvas-file-viewer__header">
      <div class="canvas-file-viewer__file">
        <span class="canvas-file-viewer__label">{{ showDiff ? "Diff:" : "File:" }}</span>
        <span
          class="canvas-file-viewer__path"
          :title="selectedFilePath ?? ''"
        >{{ selectedFilePath }}</span>
      </div>
      <div class="canvas-file-viewer__actions">
        <div
          class="canvas-file-viewer__toggle"
          role="group"
          aria-label="Content view mode"
        >
          <button
            type="button"
            class="canvas-file-viewer__toggle-btn"
            :class="{ 'canvas-file-viewer__toggle-btn--active': !showDiff }"
            :aria-pressed="!showDiff"
            aria-label="Show file"
            title="Show file"
            @click="setFileView"
          >
            <FileText class="canvas-file-viewer__icon" />
          </button>
          <button
            type="button"
            class="canvas-file-viewer__toggle-btn"
            :class="{ 'canvas-file-viewer__toggle-btn--active': showDiff }"
            :aria-pressed="showDiff"
            :disabled="!hasDiff"
            aria-label="Show diff"
            title="Show diff"
            @click="setDiffView"
          >
            <GitCompare class="canvas-file-viewer__icon" />
          </button>
        </div>
        <button
          type="button"
          class="canvas-file-viewer__close"
          data-testid="visual-panel-close"
          aria-label="Close file"
          title="Close file"
          @click="contentPanel.clearFile()"
        >
          <X class="canvas-file-viewer__icon" />
        </button>
      </div>
    </div>

    <div class="canvas-file-viewer__content">
      <DiffView
        v-if="showDiff && diffLines"
        :lines="diffLines"
      />
      <component
        :is="renderer"
        v-else-if="payload && renderer"
        :content="payload.content"
        :annotatable="isMarkdown"
        @annotate="handleAnnotate"
      />
      <p
        v-else
        class="canvas-file-viewer__loading"
      >
        Loading…
      </p>
    </div>
  </section>
</template>

<style scoped>
.canvas-file-viewer {
  display: flex;
  flex-direction: column;
  gap: 8px;
  height: 100%;
}

.canvas-file-viewer__header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  min-height: 32px;
}

.canvas-file-viewer__file {
  display: flex;
  align-items: center;
  gap: 6px;
  flex: 1;
  min-width: 0;
}

.canvas-file-viewer__label {
  flex-shrink: 0;
  font-size: 12px;
  color: var(--muted);
}

.canvas-file-viewer__path {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-family: var(--font-mono-stack);
  font-size: 12px;
  color: var(--text);
}

.canvas-file-viewer__actions {
  display: flex;
  flex-shrink: 0;
  align-items: center;
  gap: 6px;
}

.canvas-file-viewer__toggle {
  display: flex;
  align-items: center;
  gap: 2px;
  padding: 2px;
  border-radius: var(--radius-btn);
  background: color-mix(in srgb, var(--text) 5%, transparent);
}

.canvas-file-viewer__toggle-btn {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 26px;
  height: 22px;
  padding: 0;
  border: none;
  border-radius: calc(var(--radius-btn) - 2px);
  background: transparent;
  color: var(--muted);
  cursor: pointer;
  transition: background var(--transition), color var(--transition);
}

.canvas-file-viewer__toggle-btn--active {
  background: color-mix(in srgb, var(--text) 12%, transparent);
  color: var(--text);
}

.canvas-file-viewer__toggle-btn:hover:not(.canvas-file-viewer__toggle-btn--active):not(:disabled) {
  background: color-mix(in srgb, var(--text) 6%, transparent);
  color: var(--text);
}

.canvas-file-viewer__toggle-btn:disabled {
  cursor: not-allowed;
  opacity: 0.45;
}

.canvas-file-viewer__close {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 26px;
  height: 26px;
  padding: 0;
  border: 1px solid transparent;
  border-radius: var(--radius-btn);
  background: transparent;
  color: var(--muted);
  cursor: pointer;
  transition: background var(--transition), color var(--transition);
}

.canvas-file-viewer__close:hover {
  background: color-mix(in srgb, var(--text) 6%, transparent);
  color: var(--text);
}

.canvas-file-viewer__icon {
  width: 13px;
  height: 13px;
}

.canvas-file-viewer__content {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
}

.canvas-file-viewer__loading {
  margin: 0;
  padding: 12px 2px;
  font-size: 12px;
  color: var(--muted);
}

@media (prefers-reduced-motion: reduce) {
  .canvas-file-viewer__toggle-btn,
  .canvas-file-viewer__close {
    transition: none;
  }
}
</style>
