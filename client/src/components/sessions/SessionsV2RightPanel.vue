<script setup lang="ts">
import { computed, provide, ref, watch } from "vue";
import { storeToRefs } from "pinia";
import { FileText, GitCompare, PanelRightClose, X } from "lucide-vue-next";
import { Button } from "@/components/ui/button";
import AnnotationPopover from "@/components/annotations/AnnotationPopover.vue";
import CollapsedRightRail from "@/components/layout/CollapsedRightRail.vue";
import DiffView from "@/components/session/DiffView.vue";
import FileBrowserPanel from "@/components/session/FileBrowserPanel.vue";
import SessionMetadataHeader from "@/components/session/SessionMetadataHeader.vue";
import {
  useAbortSession,
  useArchiveSession,
  useDeleteSession,
  useRenameSession,
  useResumeSession,
  useTerminateSession,
} from "@/composables/use-session-actions";
import { provideSessionDetailContext } from "@/composables/use-session-detail-context";
import { useSessionTodos } from "@/composables/use-session-todos";
import { useVisualPanel } from "@/composables/use-visual-panel";
import { useAnnotation } from "@/composables/use-annotation";
import { useSendPrompt } from "@/composables/use-send-prompt";
import { useDraftState } from "@/composables/use-draft-state";
import { provideContentPanelContext } from "@/composables/use-content-panel";
import { useDiffs } from "@/composables/use-diffs";
import { useSessionsStore } from "@/stores/sessions";
import { useSidebarStore } from "@/stores/sidebar";
import { getVisualRenderer } from "@/lib/visual-renderer-registry";
import { formatAnnotationPrompt } from "@/lib/format-annotation-prompt";
import { extractAnchorText } from "@/lib/annotation-types";
import { parseDiffLines } from "@/lib/diff-parser";
import { VISUAL_SYNTHETIC_PATH_PREFIX } from "@/lib/visual-payload-path";
import type { AnnotationAnchor } from "@/lib/annotation-types";

const SYNTHETIC_PATH_PREFIX = VISUAL_SYNTHETIC_PATH_PREFIX;
const TREE_WIDTH_MIN = 180;
const TREE_WIDTH_MAX = 600;
const TREE_WIDTH_STEP = 10;

interface Props {
  width?: number;
}

const props = withDefaults(defineProps<Props>(), {
  width: 360,
});

const sidebarStore = useSidebarStore();
const sessionsStore = useSessionsStore();

const { rightPanelCollapsed } = storeToRefs(sidebarStore);
const { sessions, activeSessionId } = storeToRefs(sessionsStore);

// Visual panel state is keyed by session id; re-derive when the active session changes.
const visualPanel = computed(() => useVisualPanel(activeSessionId.value ?? ""));
const visualPayload = computed(() => visualPanel.value.visualPayload.value);
function clearVisual(): void {
  visualPanel.value.clearVisual();
}

// Provide content panel context
const contentPanelContext = provideContentPanelContext(activeSessionId);

// Create shared diffs instance and fetch on mount
const sharedDiffs = useDiffs(activeSessionId);
provide('sharedDiffs', sharedDiffs);

// Watch session ID and fetch diffs when it changes to a non-null value
watch(
  activeSessionId,
  (sessionId) => {
    if (sessionId) {
      void sharedDiffs.fetchDiffs();
    }
  },
  { immediate: true },
);

const selectedSession = computed(() =>
  sessions.value.find((s) => s.session.id === activeSessionId.value) ?? null,
);

// --- Action composables (V2) ---
const abort = useAbortSession();
const archive = useArchiveSession();
const del = useDeleteSession();
const rename = useRenameSession();
const resume = useResumeSession();
const terminate = useTerminateSession();
provideSessionDetailContext({
  apiBasePath: "/api/sessions",
  sessionRoutePath: "/sessions/$id",
  supportsFork: true,
  supportsArchive: true,
  actionsLayout: "card",
  patchSession: (id, patch) => sessionsStore.patchSession(id, patch),
  abort,
  archive,
  delete: del,
  rename,
  resume,
  terminate,
});

// --- Collapsed rail: todos ---
const { todos } = useSessionTodos(
  computed(() => activeSessionId.value ?? ""),
);

function handleExpand(): void {
  sidebarStore.setRightPanelCollapsed(false);
}

function handleCollapse(): void {
  sidebarStore.setRightPanelCollapsed(true);
}

const visualRenderer = computed(() => {
  if (!visualPayload.value) return null;
  return getVisualRenderer(visualPayload.value.$type);
});

// --- Annotation flow ---
// We need to initialize these composables with the active session ID
// Since the session ID can change, we'll handle the case where there's no active session
const currentSessionId = computed(() => activeSessionId.value ?? "");

const {
  activeAnchor,
  isPopoverOpen,
  popoverPosition,
  openAnnotation,
  closeAnnotation,
  submitAnnotation,
} = useAnnotation({
  onSubmit: (formattedText: string) => {
    const sessionId = currentSessionId.value;
    if (!sessionId) return;

    // Get the composables for the current session
    const { sendPrompt } = useSendPrompt(sessionId);
    const { setText } = useDraftState(sessionId, {
      agentId: "",
      modelId: "",
    });

    // Format the annotation prompt with file path if available
    const filePath = visualPayload.value?.sourceFilePath ?? "";
    const anchorText = activeAnchor.value ? extractAnchorText(activeAnchor.value) : "";
    const prompt = formatAnnotationPrompt(filePath, anchorText, formattedText);

    // Set the draft text and send
    setText(prompt);
    sendPrompt();
  },
});

function handleAnnotate(anchor: AnnotationAnchor, position: { x: number; y: number }): void {
  openAnnotation(anchor, position);
}

function handleAnnotationSend(text: string): void {
  submitAnnotation(text);
}

function handleAnnotationCancel(): void {
  closeAnnotation();
}

// Check if the current visual renderer is the MarkdownRenderer
const isMarkdownRenderer = computed(() => {
  return visualPayload.value?.$type === "markdown";
});

// --- Content-slot routing ---
const selectedFilePath = computed(() => contentPanelContext.filesContext.value.selectedFilePath);
const isSyntheticPath = computed(() => selectedFilePath.value?.startsWith(SYNTHETIC_PATH_PREFIX) ?? false);

// Check if the selected file has diff data available
const selectedFileDiff = computed(() => {
  const selectedPath = selectedFilePath.value;
  if (!selectedPath || isSyntheticPath.value) return null;

  return sharedDiffs.diffs.value.find(d => d.file === selectedPath) ?? null;
});

// Parse diff lines for the selected file
const diffLines = computed(() => {
  if (!selectedFileDiff.value) return null;

  return parseDiffLines(selectedFileDiff.value.before, selectedFileDiff.value.after);
});

// Does a diff exist for the selected file?
const hasDiff = computed(() => diffLines.value !== null);

// Show the diff view when the user has toggled to it AND a diff exists.
const shouldShowDiff = computed(() =>
  contentPanelContext.viewMode.value === "diff" && hasDiff.value && !isSyntheticPath.value,
);

function setFileView(): void {
  contentPanelContext.setViewMode("file");
}

function setDiffView(): void {
  if (!hasDiff.value) return;
  contentPanelContext.setViewMode("diff");
}

// --- Files-tree width resize gutter ---
const isGutterDragging = ref(false);

function onGutterPointerDown(e: PointerEvent): void {
  isGutterDragging.value = true;
  const startX = e.clientX;
  const startWidth = contentPanelContext.filesContext.value.filesTreeWidth;
  document.body.style.cursor = "col-resize";
  document.body.style.userSelect = "none";

  const onMove = (ev: PointerEvent) => {
    const delta = ev.clientX - startX;
    const nextWidth = Math.max(TREE_WIDTH_MIN, Math.min(TREE_WIDTH_MAX, startWidth + delta));
    contentPanelContext.updateFilesContext({ filesTreeWidth: nextWidth });
  };

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
  const current = contentPanelContext.filesContext.value.filesTreeWidth;

  if (e.key === "ArrowLeft") {
    e.preventDefault();
    contentPanelContext.updateFilesContext({ filesTreeWidth: Math.max(TREE_WIDTH_MIN, current - TREE_WIDTH_STEP) });
  } else if (e.key === "ArrowRight") {
    e.preventDefault();
    contentPanelContext.updateFilesContext({ filesTreeWidth: Math.min(TREE_WIDTH_MAX, current + TREE_WIDTH_STEP) });
  } else if (e.key === "Home") {
    e.preventDefault();
    contentPanelContext.updateFilesContext({ filesTreeWidth: TREE_WIDTH_MIN });
  } else if (e.key === "End") {
    e.preventDefault();
    contentPanelContext.updateFilesContext({ filesTreeWidth: TREE_WIDTH_MAX });
  }
}
</script>

<template>
  <CollapsedRightRail
    v-if="rightPanelCollapsed"
    :todos="todos"
    @expand="handleExpand"
  />

  <aside
    v-else
    class="right-panel"
    :style="{ width: `${props.width}px`, minWidth: '280px' }"
    aria-label="Right panel"
  >
    <Button
      variant="toolbar-icon"
      size="toolbar"
      class="right-panel__collapse"
      aria-label="Collapse right panel"
      title="Collapse right panel"
      @click="handleCollapse"
    >
      <PanelRightClose />
    </Button>

    <SessionMetadataHeader :session="selectedSession" />

    <div class="right-content">
      <div
        class="right-content__split"
        :style="{ '--files-tree-width': `${contentPanelContext.filesContext.value.filesTreeWidth}px` }"
      >
        <div class="right-content__left">
          <FileBrowserPanel :session-id="activeSessionId ?? ''" />
        </div>

        <div
          class="right-content__gutter"
          role="separator"
          aria-orientation="vertical"
          aria-label="Resize files panel"
          :aria-valuenow="contentPanelContext.filesContext.value.filesTreeWidth"
          :aria-valuemin="TREE_WIDTH_MIN"
          :aria-valuemax="TREE_WIDTH_MAX"
          tabindex="0"
          :class="{ 'right-content__gutter--dragging': isGutterDragging }"
          @pointerdown="onGutterPointerDown"
          @keydown="onGutterKeydown"
        />

        <div class="right-content__right">
          <!-- Visual (VP4) mirror: synthetic __visual__/... paths -->
          <section
            v-if="isSyntheticPath && visualPayload && visualRenderer"
            class="visual-panel"
          >
            <div class="visual-panel__header">
              <div
                v-if="visualPayload.sourceFilePath"
                class="visual-panel__file-info"
              >
                <span class="visual-panel__file-label">File:</span>
                <span class="visual-panel__file-path">{{ visualPayload.sourceFilePath }}</span>
              </div>
              <h2
                v-else
                class="visual-panel__title"
              >
                {{ visualPayload.title ?? 'Visual Content' }}
              </h2>
              <button
                class="visual-panel__close"
                data-testid="visual-panel-close"
                aria-label="Close preview"
                @click="clearVisual"
              >
                <X class="visual-panel__close-icon" />
              </button>
            </div>
            <div class="visual-panel__content">
              <component
                :is="visualRenderer"
                :content="visualPayload.content"
                :annotatable="isMarkdownRenderer"
                @annotate="handleAnnotate"
              />
            </div>
          </section>

          <!-- File / Diff viewer for a real selected file -->
          <section
            v-else-if="visualPayload && visualRenderer"
            class="visual-panel"
          >
            <div class="visual-panel__header">
              <div class="visual-panel__file-info">
                <span class="visual-panel__file-label">{{ shouldShowDiff ? 'Diff:' : 'File:' }}</span>
                <span class="visual-panel__file-path">{{ visualPayload.sourceFilePath ?? visualPayload.title ?? '' }}</span>
              </div>
              <div class="visual-panel__actions">
                <div class="visual-panel__toggle" role="group" aria-label="Content view mode">
                  <button
                    class="visual-panel__toggle-btn"
                    :class="{ 'visual-panel__toggle-btn--active': !shouldShowDiff }"
                    :aria-pressed="!shouldShowDiff"
                    aria-label="Show file"
                    title="Show file"
                    @click="setFileView"
                  >
                    <FileText class="visual-panel__toggle-icon" />
                  </button>
                  <button
                    class="visual-panel__toggle-btn"
                    :class="{ 'visual-panel__toggle-btn--active': shouldShowDiff }"
                    :aria-pressed="shouldShowDiff"
                    :disabled="!hasDiff"
                    aria-label="Show diff"
                    title="Show diff"
                    @click="setDiffView"
                  >
                    <GitCompare class="visual-panel__toggle-icon" />
                  </button>
                </div>
                <button
                  class="visual-panel__close"
                  data-testid="visual-panel-close"
                  aria-label="Close preview"
                  @click="clearVisual"
                >
                  <X class="visual-panel__close-icon" />
                </button>
              </div>
            </div>
            <div class="visual-panel__content">
              <DiffView v-if="shouldShowDiff && diffLines" :lines="diffLines" />
              <component
                v-else
                :is="visualRenderer"
                :content="visualPayload.content"
                :annotatable="isMarkdownRenderer"
                @annotate="handleAnnotate"
              />
            </div>
          </section>

          <div v-else class="right-content__empty">
            <p class="right-content__empty-text">
              Select a file to view its content.
            </p>
          </div>
        </div>
      </div>
    </div>

    <!-- Annotation Popover -->
    <Teleport to="body">
      <AnnotationPopover
        v-if="isPopoverOpen && activeAnchor"
        :x="popoverPosition.x"
        :y="popoverPosition.y"
        :anchor-text="extractAnchorText(activeAnchor)"
        @send="handleAnnotationSend"
        @cancel="handleAnnotationCancel"
      />
    </Teleport>
  </aside>
</template>

<style scoped>
.right-panel {
  position: relative;
  min-height: 0;
  background: var(--panel-bg);
  border: 1px solid var(--border);
  border-radius: var(--radius-panel);
  display: flex;
  flex-direction: column;
  overflow: hidden;
}

.right-panel__collapse {
  position: absolute;
  top: 8px;
  right: 8px;
  z-index: 2;
}

.right-content {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
  overflow: hidden;
}

.right-content__split {
  flex: 1;
  min-height: 0;
  display: flex;
  overflow: hidden;
}

.right-content__left {
  width: var(--files-tree-width, 260px);
  flex: 0 0 auto;
  min-height: 0;
  overflow-y: auto;
  border-right: 1px solid var(--border);
}

.right-content__gutter {
  flex: 0 0 4px;
  width: 4px;
  cursor: col-resize;
  background: transparent;
  transition: background var(--transition);
}

@media (prefers-reduced-motion: reduce) {
  .right-content__gutter {
    transition: none;
  }
}

.right-content__gutter:hover,
.right-content__gutter--dragging {
  background: var(--accent);
}

.right-content__gutter:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -2px;
}

.right-content__right {
  flex: 1;
  min-width: 0;
  min-height: 0;
  overflow-y: auto;
  padding: 10px;
}

.right-content__empty {
  display: flex;
  align-items: center;
  justify-content: center;
  height: 100%;
  padding: 24px;
}

.right-content__empty-text {
  margin: 0;
  font-size: 12px;
  color: var(--muted);
  text-align: center;
}

.right-content__left [role="tabpanel"]:focus {
  outline: none;
}

.session-artifacts {
  padding: 8px;
  border-bottom: 1px solid var(--border);
}

.session-artifacts__title {
  margin: 0 0 6px;
  font-size: 10px;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.5px;
  color: var(--muted);
}

.session-artifacts__item {
  display: block;
  width: 100%;
  padding: 6px 8px;
  text-align: left;
  background: transparent;
  border: 1px solid transparent;
  border-radius: 4px;
  cursor: pointer;
  font-size: 12px;
  color: var(--text);
  transition: background-color var(--transition), border-color var(--transition);
}

.session-artifacts__item:hover {
  background: rgba(255, 255, 255, 0.03);
}

.session-artifacts__item--selected {
  border-color: color-mix(in srgb, var(--accent) 42%, var(--border));
  background: color-mix(in srgb, var(--accent) 14%, transparent);
}

.visual-panel {
  display: flex;
  flex-direction: column;
  gap: 12px;
  height: 100%;
}

.visual-panel__header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  padding-bottom: 8px;
  border-bottom: 1px solid var(--border);
}

.visual-panel__file-info {
  display: flex;
  align-items: center;
  gap: 6px;
  flex: 1;
  min-width: 0;
}

.visual-panel__file-label {
  font-size: 10px;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.5px;
  color: var(--muted);
  flex-shrink: 0;
}

.visual-panel__file-path {
  font-size: 12px;
  font-family: monospace;
  color: var(--text);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.visual-panel__title {
  margin: 0;
  font-size: 14px;
  font-weight: 600;
  color: var(--text);
}

.visual-panel__close {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 24px;
  height: 24px;
  padding: 0;
  border: 1px solid var(--border);
  border-radius: 0;
  background: transparent;
  color: var(--muted);
  cursor: pointer;
  transition: background var(--transition), color var(--transition), border-color var(--transition);
}

@media (prefers-reduced-motion: reduce) {
  .visual-panel__close {
    transition: none;
  }
}

.visual-panel__close:hover {
  background: color-mix(in srgb, var(--text) 8%, transparent);
  border-color: color-mix(in srgb, var(--text) 25%, var(--border));
  color: var(--text);
}

.visual-panel__close-icon {
  width: 14px;
  height: 14px;
}

.visual-panel__content {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
}

.visual-panel__actions {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-shrink: 0;
}

.visual-panel__toggle {
  display: flex;
  align-items: center;
  border: 1px solid var(--border);
  border-radius: 0;
  overflow: hidden;
}

.visual-panel__toggle-btn {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 26px;
  height: 24px;
  padding: 0;
  border: none;
  background: transparent;
  color: var(--muted);
  cursor: pointer;
  transition: background var(--transition), color var(--transition);
}

.visual-panel__toggle-btn:not(:last-child) {
  border-right: 1px solid var(--border);
}

.visual-panel__toggle-btn--active {
  background: color-mix(in srgb, var(--text) 12%, transparent);
  color: var(--text);
}

.visual-panel__toggle-btn:hover:not(.visual-panel__toggle-btn--active) {
  background: color-mix(in srgb, var(--text) 6%, transparent);
  color: var(--text);
}

.visual-panel__toggle-icon {
  width: 13px;
  height: 13px;
}
</style>
