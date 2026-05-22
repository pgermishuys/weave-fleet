<script setup lang="ts">
import { computed, onUnmounted, shallowRef, watch } from "vue";
import { Info, Rows3, RotateCcw } from "lucide-vue-next";
import FilesChangedFileList from "@/components/session/FilesChangedFileList.vue";
import DiffView from "@/components/session/DiffView.vue";
import type { FilesChangedFileListItem } from "@/components/session/FilesChangedFileList.vue";
import { Sheet, SheetContent, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { useSessionDiffsContext } from "@/composables/use-session-diffs-context";
import { parseDiffLines } from "@/lib/diff-parser";

export type DiffsTrayFileItem = FilesChangedFileListItem;

const props = defineProps<{
  open: boolean;
  selectedFile?: DiffsTrayFileItem | null;
}>();

const emit = defineEmits<{
  "update:open": [open: boolean];
  select: [file: DiffsTrayFileItem];
  retry: [];
}>();

const sessionDiffsContext = useSessionDiffsContext();
const diffState = computed(() => sessionDiffsContext.value?.diffState ?? null);

const MIN_TRAY_WIDTH = 720;
const MAX_TRAY_WIDTH_RATIO = 1;
const DEFAULT_TRAY_WIDTH_RATIO = 0.8;
const DEFAULT_TRAY_MAX_WIDTH = 2048;
const RESIZE_KEY_STEP = 40;

const localSelectedFile = shallowRef<DiffsTrayFileItem | null>(null);
const lineWrap = shallowRef(true);
const trayWidth = shallowRef<number | null>(typeof window === "undefined" ? null : getDefaultTrayWidth());
const isResizingTray = shallowRef(false);
const previousBodyCursor = shallowRef<string | null>(null);
const previousBodyUserSelect = shallowRef<string | null>(null);

const files = computed(() => diffState.value?.diffs.value ?? []);
const isLoading = computed(() => diffState.value?.isLoading.value ?? false);
const error = computed(() => diffState.value?.error.value ?? null);
const unavailable = computed(() => diffState.value !== null && !isLoading.value && !error.value && !diffState.value.available.value);
const selectedFile = computed(() => props.selectedFile ?? localSelectedFile.value);
const selectedFilePath = computed(() => selectedFile.value?.file ?? null);
const trayStyle = computed(() => {
  if (trayWidth.value === null) {
    return undefined;
  }

  return {
    maxWidth: `${trayWidth.value}px`,
    width: `${trayWidth.value}px`,
  };
});
const mainState = computed<"loading" | "unavailable" | "error" | "empty" | "diff">(() => {
  if (isLoading.value) {
    return "loading";
  }

  if (unavailable.value) {
    return "unavailable";
  }

  if (error.value) {
    return "error";
  }

  if (files.value.length === 0) {
    return "empty";
  }

  return "diff";
});

const selectedFileContent = computed(() => {
  if (selectedFileDiffPlaceholder.value !== null) {
    return null;
  }

  const before = typeof selectedFile.value?.before === "string" ? selectedFile.value.before : "";
  const after = typeof selectedFile.value?.after === "string" ? selectedFile.value.after : "";

  if (before.length === 0 && after.length === 0) {
    return null;
  }

  return { before, after };
});

const selectedFileDiffPlaceholder = computed(() => {
  if (selectedFile.value === null) {
    return null;
  }

  if (selectedFile.value.isBinary === true || selectedFile.value.binary === true) {
    return "Binary file — no diff available";
  }

  if (selectedFile.value.isTruncated === true || selectedFile.value.truncated === true) {
    return "File too large to diff";
  }

  return null;
});

const selectedFileDiffLines = computed(() => {
  if (selectedFileContent.value === null) {
    return [];
  }

  return parseDiffLines(selectedFileContent.value.before, selectedFileContent.value.after);
});

watch(
  () => props.selectedFile,
  (file) => {
    if (file !== null && file !== undefined) {
      localSelectedFile.value = file;
    }
  },
  { immediate: true },
);

watch(
  files,
  (nextFiles) => {
    if (localSelectedFile.value !== null && nextFiles.some((file) => file.file === localSelectedFile.value?.file)) {
      return;
    }

    localSelectedFile.value = nextFiles[0] ?? null;
  },
  { immediate: true },
);

function updateOpen(open: boolean): void {
  emit("update:open", open);
}

function selectFile(file: DiffsTrayFileItem): void {
  if (isLoading.value || unavailable.value || error.value) {
    return;
  }

  localSelectedFile.value = file;
  emit("select", file);
}

function retryLoadingFiles(): void {
  emit("retry");
}

function getMaxTrayWidth(): number {
  return Math.max(MIN_TRAY_WIDTH, Math.floor(window.innerWidth * MAX_TRAY_WIDTH_RATIO));
}

function getDefaultTrayWidth(): number {
  return Math.min(Math.floor(window.innerWidth * DEFAULT_TRAY_WIDTH_RATIO), DEFAULT_TRAY_MAX_WIDTH, getMaxTrayWidth());
}

function clampTrayWidth(width: number): number {
  return Math.min(Math.max(Math.floor(width), MIN_TRAY_WIDTH), getMaxTrayWidth());
}

function resizeTrayToClientX(clientX: number): void {
  trayWidth.value = clampTrayWidth(window.innerWidth - clientX);
}

function resizeTrayBy(delta: number): void {
  trayWidth.value = clampTrayWidth((trayWidth.value ?? getDefaultTrayWidth()) + delta);
}

function handleTrayResizeMove(event: MouseEvent): void {
  resizeTrayToClientX(event.clientX);
}

function handleTrayTouchResizeMove(event: TouchEvent): void {
  const touch = event.touches[0];
  if (touch === undefined) {
    return;
  }

  event.preventDefault();
  resizeTrayToClientX(touch.clientX);
}

function stopTrayResize(): void {
  if (!isResizingTray.value) {
    return;
  }

  window.removeEventListener("mousemove", handleTrayResizeMove);
  window.removeEventListener("mouseup", stopTrayResize);
  window.removeEventListener("touchmove", handleTrayTouchResizeMove);
  window.removeEventListener("touchend", stopTrayResize);
  window.removeEventListener("touchcancel", stopTrayResize);

  document.body.style.cursor = previousBodyCursor.value ?? "";
  document.body.style.userSelect = previousBodyUserSelect.value ?? "";
  previousBodyCursor.value = null;
  previousBodyUserSelect.value = null;
  isResizingTray.value = false;
}

function startTrayResize(clientX: number): void {
  if (window.innerWidth <= 760) {
    return;
  }

  resizeTrayToClientX(clientX);
  isResizingTray.value = true;
  previousBodyCursor.value = document.body.style.cursor;
  previousBodyUserSelect.value = document.body.style.userSelect;
  document.body.style.cursor = "ew-resize";
  document.body.style.userSelect = "none";
  window.addEventListener("mousemove", handleTrayResizeMove);
  window.addEventListener("mouseup", stopTrayResize);
  window.addEventListener("touchmove", handleTrayTouchResizeMove, { passive: false });
  window.addEventListener("touchend", stopTrayResize);
  window.addEventListener("touchcancel", stopTrayResize);
}

function handleResizeMouseDown(event: MouseEvent): void {
  event.preventDefault();
  event.stopPropagation();
  startTrayResize(event.clientX);
}

function handleResizeTouchStart(event: TouchEvent): void {
  const touch = event.touches[0];
  if (touch === undefined) {
    return;
  }

  event.preventDefault();
  event.stopPropagation();
  startTrayResize(touch.clientX);
}

function handleResizeHandleKeydown(event: KeyboardEvent): void {
  if (event.key === "ArrowLeft") {
    event.preventDefault();
    resizeTrayBy(RESIZE_KEY_STEP);
    return;
  }

  if (event.key === "ArrowRight") {
    event.preventDefault();
    resizeTrayBy(-RESIZE_KEY_STEP);
    return;
  }

  if (event.key === "Home") {
    event.preventDefault();
    trayWidth.value = MIN_TRAY_WIDTH;
    return;
  }

  if (event.key === "End") {
    event.preventDefault();
    trayWidth.value = getMaxTrayWidth();
  }
}

onUnmounted(stopTrayResize);
</script>

<template>
  <Sheet
    :open="open"
    @update:open="updateOpen"
  >
    <SheetContent
      side="right"
      class="diffs-tray"
      :style="trayStyle"
    >
      <div
        class="diffs-tray__resize-handle"
        role="separator"
        aria-label="Resize file diff tray"
        aria-orientation="vertical"
        tabindex="0"
        @mousedown="handleResizeMouseDown"
        @touchstart="handleResizeTouchStart"
        @keydown="handleResizeHandleKeydown"
      />
      <SheetHeader class="diffs-tray__header">
        <div class="diffs-tray__heading-row">
          <div class="diffs-tray__heading-copy">
            <SheetTitle class="diffs-tray__title">
              File diffs
            </SheetTitle>
            <p class="diffs-tray__description">
              Review this session's file changes.
            </p>
          </div>

          <div class="diffs-tray__toolbar">
            <button
              type="button"
              class="diffs-tray__toolbar-button"
              :aria-pressed="lineWrap"
              title="Toggle line wrap"
              @click="lineWrap = !lineWrap"
            >
              <Rows3 aria-hidden="true" />
              <span class="sr-only">Toggle line wrap</span>
            </button>
            <button
              type="button"
              class="diffs-tray__toolbar-button"
              title="Retry loading diffs"
              @click="retryLoadingFiles"
            >
              <RotateCcw aria-hidden="true" />
              <span class="sr-only">Retry loading diffs</span>
            </button>
          </div>
        </div>
      </SheetHeader>

      <div class="diffs-tray__body">
        <aside
          class="diffs-tray__file-list"
          aria-label="Changed file list"
        >
          <FilesChangedFileList
            :files="files"
            :selected-file="selectedFile"
            :is-loading="isLoading"
            :error="error"
            :unavailable="unavailable"
            variant="full"
            @select="selectFile"
          />
        </aside>

        <main
          class="diffs-tray__diff"
          aria-label="Selected file diff"
        >
          <header
            v-if="mainState === 'diff' && selectedFilePath !== null"
            class="diffs-tray__diff-header"
          >
            <p
              class="diffs-tray__selected-path"
              :title="selectedFilePath"
            >
              {{ selectedFilePath }}
            </p>
          </header>

          <div
            class="diffs-tray__diff-body"
            :class="{ 'diffs-tray__diff-body--nowrap': !lineWrap }"
          >
            <div
              v-if="mainState === 'loading'"
              class="diffs-tray__state"
              role="status"
              aria-live="polite"
              aria-busy="true"
            >
              <span
                class="diffs-tray__spinner"
                aria-hidden="true"
              />
              <p>Loading diffs…</p>
            </div>

            <div
              v-else-if="mainState === 'unavailable'"
              class="diffs-tray__state"
              role="status"
            >
              <Info aria-hidden="true" />
              <p>Diffs are unavailable for this session.</p>
            </div>

            <div
              v-else-if="mainState === 'error'"
              class="diffs-tray__state diffs-tray__state--error"
              role="alert"
            >
              <Info aria-hidden="true" />
              <p>{{ error }}</p>
            </div>

            <div
              v-else-if="mainState === 'empty'"
              class="diffs-tray__state"
              role="status"
            >
              <Info aria-hidden="true" />
              <p>No files changed.</p>
            </div>

            <div
              v-else-if="selectedFileDiffPlaceholder !== null"
              class="diffs-tray__state"
              role="status"
            >
              <Info aria-hidden="true" />
              <p>{{ selectedFileDiffPlaceholder }}</p>
            </div>

            <DiffView
              v-else-if="selectedFileContent !== null"
              :lines="selectedFileDiffLines"
            />

            <div
              v-else
              class="diffs-tray__state"
              role="status"
            >
              <Info aria-hidden="true" />
              <p>Diff content is unavailable for this file.</p>
            </div>
          </div>
        </main>
      </div>
    </SheetContent>
  </Sheet>
</template>

<style scoped>
:deep(.diffs-tray) {
  width: min(80vw, 2048px);
  max-width: 100vw;
  min-width: 720px;
  gap: 0;
  padding: 0;
  background: var(--bg);
  color: var(--text);
}

.diffs-tray__resize-handle {
  position: absolute;
  top: 0;
  bottom: 0;
  left: 0;
  z-index: 10;
  width: 12px;
  cursor: ew-resize;
  touch-action: none;
  user-select: none;
}

.diffs-tray__resize-handle::after {
  position: absolute;
  top: 0;
  bottom: 0;
  left: 0;
  width: 2px;
  background: transparent;
  content: "";
  transition: background 120ms ease;
}

.diffs-tray__resize-handle:hover::after,
.diffs-tray__resize-handle:focus-visible::after {
  background: color-mix(in srgb, var(--accent) 64%, transparent);
}

.diffs-tray__header {
  flex: 0 0 auto;
  border-bottom: 1px solid var(--border);
  background: color-mix(in srgb, var(--panel) 82%, transparent);
  padding: 12px 48px 12px 16px;
}

.diffs-tray__heading-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
}

.diffs-tray__heading-copy {
  min-width: 0;
}

.diffs-tray__title,
.diffs-tray__description,
.diffs-tray__selected-path,
.diffs-tray__state p {
  margin: 0;
}

.diffs-tray__title {
  color: var(--text);
  font-size: 15px;
  font-weight: 700;
}

.diffs-tray__description {
  margin-top: 2px;
  color: var(--muted);
  font-size: 12px;
}

.diffs-tray__toolbar {
  display: flex;
  flex: 0 0 auto;
  align-items: center;
  gap: 6px;
}

.diffs-tray__toolbar-button {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 28px;
  height: 28px;
  border: 1px solid var(--border);
  border-radius: 7px;
  background: transparent;
  color: var(--text);
  cursor: pointer;
}

.diffs-tray__toolbar-button[aria-pressed="true"] {
  border-color: color-mix(in srgb, var(--accent) 46%, var(--border));
  background: color-mix(in srgb, var(--accent) 12%, transparent);
  color: var(--accent);
}

.diffs-tray__toolbar-button:hover {
  background: color-mix(in srgb, var(--panel) 74%, var(--text) 8%);
}

.diffs-tray__toolbar-button svg {
  width: 14px;
  height: 14px;
}

.diffs-tray__body {
  display: grid;
  min-height: 0;
  flex: 1 1 auto;
  grid-template-columns: minmax(220px, 280px) minmax(0, 1fr);
}

.diffs-tray__file-list {
  min-height: 0;
  min-width: 0;
  overflow-y: auto;
  border-right: 1px solid var(--border);
  background: color-mix(in srgb, var(--panel) 74%, var(--bg));
}

.diffs-tray__diff {
  display: flex;
  min-height: 0;
  min-width: 0;
  flex-direction: column;
  overflow: hidden;
}

.diffs-tray__diff-header {
  flex: 0 0 auto;
  padding: 10px 14px;
  border-bottom: 1px solid var(--border);
  background: color-mix(in srgb, var(--panel) 82%, transparent);
}

.diffs-tray__selected-path {
  overflow: hidden;
  color: var(--text);
  font-family: ui-monospace, SFMono-Regular, Consolas, "Liberation Mono", Menlo, monospace;
  font-size: 12px;
  font-weight: 600;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.diffs-tray__diff-body {
  min-height: 0;
  flex: 1 1 auto;
  overflow: auto;
}

.diffs-tray__diff-body--nowrap :deep(.diff-line__content) {
  white-space: pre;
}

.diffs-tray__state {
  display: flex;
  min-height: 100%;
  align-items: center;
  justify-content: center;
  gap: 10px;
  padding: 24px;
  color: var(--muted);
  font-size: 13px;
  text-align: center;
}

.diffs-tray__state svg {
  width: 16px;
  height: 16px;
  flex: 0 0 auto;
}

.diffs-tray__state--error {
  color: color-mix(in srgb, var(--error) 76%, var(--text));
}

.diffs-tray__spinner {
  width: 18px;
  height: 18px;
  flex: 0 0 auto;
  border: 2px solid color-mix(in srgb, var(--muted) 28%, transparent);
  border-top-color: var(--accent);
  border-radius: 999px;
  animation: diffs-tray-spin 0.8s linear infinite;
}

@media (width <= 760px) {
  :deep(.diffs-tray) {
    width: 100vw !important;
    max-width: 100vw !important;
    min-width: 0 !important;
  }

  .diffs-tray__resize-handle {
    display: none;
  }

  .diffs-tray__body {
    grid-template-columns: minmax(150px, 38vw) minmax(0, 1fr);
  }
}

@keyframes diffs-tray-spin {
  to {
    transform: rotate(360deg);
  }
}
</style>
