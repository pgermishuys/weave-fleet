<script setup lang="ts">
import { ref } from "vue";
import { useContentPanelContext } from "@/composables/use-content-panel";

const LIST_HEIGHT_MIN = 180;
const LIST_HEIGHT_MAX = 600;
const LIST_HEIGHT_STEP = 10;

defineProps<{
  showViewer: boolean;
}>();

const contentPanel = useContentPanelContext();
const isGutterDragging = ref(false);

function setListHeight(height: number): void {
  contentPanel.updateFilesContext({
    filesTreeWidth: Math.max(LIST_HEIGHT_MIN, Math.min(LIST_HEIGHT_MAX, height)),
  });
}

function onGutterPointerDown(e: PointerEvent): void {
  isGutterDragging.value = true;
  const startY = e.clientY;
  const startHeight = contentPanel.filesContext.value.filesTreeWidth;
  document.body.style.cursor = "row-resize";
  document.body.style.userSelect = "none";

  const onMove = (ev: PointerEvent) => setListHeight(startHeight + ev.clientY - startY);

  const onUp = () => {
    isGutterDragging.value = false;
    document.body.style.cursor = "";
    document.body.style.userSelect = "";
    document.removeEventListener("pointermove", onMove);
    document.removeEventListener("pointerup", onUp);
  };

  document.addEventListener("pointermove", onMove);
  document.addEventListener("pointerup", onUp);
}

function onGutterKeydown(e: KeyboardEvent): void {
  const current = contentPanel.filesContext.value.filesTreeWidth;

  if (e.key === "ArrowUp" || e.key === "ArrowLeft") {
    e.preventDefault();
    setListHeight(current - LIST_HEIGHT_STEP);
  } else if (e.key === "ArrowDown" || e.key === "ArrowRight") {
    e.preventDefault();
    setListHeight(current + LIST_HEIGHT_STEP);
  } else if (e.key === "Home") {
    e.preventDefault();
    setListHeight(LIST_HEIGHT_MIN);
  } else if (e.key === "End") {
    e.preventDefault();
    setListHeight(LIST_HEIGHT_MAX);
  }
}
</script>

<template>
  <div
    class="canvas-split"
    :class="{ 'canvas-split--with-viewer': showViewer }"
    :style="{ '--canvas-list-height': `${contentPanel.filesContext.value.filesTreeWidth}px` }"
  >
    <div class="canvas-split__list">
      <slot name="list" />
    </div>

    <template v-if="showViewer">
      <div
        class="canvas-split__gutter"
        role="separator"
        aria-orientation="horizontal"
        aria-label="Resize list"
        :aria-valuenow="contentPanel.filesContext.value.filesTreeWidth"
        :aria-valuemin="LIST_HEIGHT_MIN"
        :aria-valuemax="LIST_HEIGHT_MAX"
        tabindex="0"
        :class="{ 'canvas-split__gutter--dragging': isGutterDragging }"
        @pointerdown="onGutterPointerDown"
        @keydown="onGutterKeydown"
      />

      <div class="canvas-split__viewer">
        <slot name="viewer" />
      </div>
    </template>
  </div>
</template>

<style scoped>
.canvas-split {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
  overflow: hidden;
}

.canvas-split__list {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
}

/* With a file open, the list keeps its persisted height and the viewer takes the rest. */
.canvas-split--with-viewer .canvas-split__list {
  flex: 0 0 auto;
  height: var(--canvas-list-height, 260px);
  max-height: 70%;
}

.canvas-split__gutter {
  flex: 0 0 5px;
  height: 5px;
  margin: 0 12px;
  border-top: 1px solid var(--border);
  cursor: row-resize;
  background: transparent;
  transition: background var(--transition);
}

.canvas-split__gutter:hover,
.canvas-split__gutter--dragging {
  background: var(--accent);
}

.canvas-split__gutter:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -2px;
}

.canvas-split__viewer {
  flex: 1;
  min-width: 0;
  min-height: 0;
  overflow-y: auto;
  padding: 4px 12px 12px;
}

@media (prefers-reduced-motion: reduce) {
  .canvas-split__gutter {
    transition: none;
  }
}
</style>
