<script setup lang="ts">
import { computed, inject, nextTick, onActivated, onBeforeUnmount, onDeactivated, onMounted, ref, shallowRef, watch } from "vue";
import { ChevronRight, MessageSquarePlus } from "lucide-vue-next";
import { Transaction, type Text } from "@codemirror/state";
import { EditorView, type ViewUpdate } from "@codemirror/view";
import type { UseDiffsResult } from "@/composables/use-diffs";
import { useCanvasAnnotate } from "@/composables/use-canvas-annotation";
import { appendDraftReference } from "@/composables/use-draft-state";
import type { AnnotationAnchor } from "@/lib/annotation-types";
import { dispatchCommandEvent } from "@/lib/command-events";
import { baseText } from "@/lib/code-editor/agent-lines";
import { finishCompare, openBuffer, saveBuffer, useDiskVersion } from "@/lib/code-editor/buffers";
import { createDeletedView, markStripe, setMerge } from "@/lib/code-editor/merge";
import { getVisualRenderer } from "@/lib/visual-renderer-registry";
import { hasRenderedView, useCanvasesStore, type FileView } from "@/stores/canvases";
import { useFileBuffersStore, type FileBufferRecord } from "@/stores/file-buffers";
import { useGoToFileStore } from "@/stores/go-to-file";

const props = defineProps<{
  sessionId: string;
  path: string;
  view: FileView;
}>();

const buffers = useFileBuffersStore();
const canvases = useCanvasesStore();
const sharedDiffs = inject<UseDiffsResult | null>("sharedDiffs", null);
const annotate = useCanvasAnnotate();
const goToFile = useGoToFileStore();

const info = computed(() => buffers.info(props.sessionId, props.path));
const ready = computed(() => info.value?.status === "ready");
const conflict = computed(() => info.value?.conflict ?? null);
const fileName = computed(() => props.path.slice(props.path.lastIndexOf("/") + 1));
const folders = computed(() => props.path.split("/").slice(0, -1));
const isMac = typeof navigator !== "undefined" && /Mac|iPhone|iPad/.test(navigator.platform);
const saveKey = isMac ? "⌘S" : "Ctrl S";

// ─── What's shown ────────────────────────────────────────────────────────────

const renderable = computed(() => hasRenderedView(props.path));
const isHtml = computed(() => /\.html?$/i.test(props.path));
const diffItem = computed(() => sharedDiffs?.diffs.value.find((diff) => diff.file === props.path) ?? null);
const gitBase = computed<Text | null>(() => (diffItem.value ? baseText(diffItem.value.before ?? "") : null));
const hasChanges = computed(() => diffItem.value !== null || (info.value?.dirty ?? false));
const deleted = computed(() => diffItem.value?.status === "deleted" && info.value?.status === "error");

// Compare is a mode of the conflict bar, on top of whichever view the tab is in.
const comparing = ref(false);
const view = computed<FileView>(() => (props.view === "diff" && !hasChanges.value ? "edit" : props.view));
const showEditor = computed(() => ready.value && (comparing.value || view.value !== "rendered"));
const showRendered = computed(() => ready.value && !comparing.value && view.value === "rendered" && renderable.value);

function setView(next: FileView): void {
  canvases.setFileView(props.sessionId, props.path, next);
}

// ─── The editor ──────────────────────────────────────────────────────────────

const editorHost = ref<HTMLElement | null>(null);
const deletedHost = ref<HTMLElement | null>(null);
const root = ref<HTMLElement | null>(null);
let editor: EditorView | null = null;
let deletedView: EditorView | null = null;
let attached: FileBufferRecord | null = null;
/** Bumped when the buffer's text changes, so Rendered follows unsaved edits. */
const revision = ref(0);

function record(): FileBufferRecord | undefined {
  return buffers.record(props.sessionId, props.path);
}

let stripeTimer: ReturnType<typeof setTimeout> | undefined;

function refreshStripe(): void {
  clearTimeout(stripeTimer);
  const current = record();
  if (current) markStripe(current, gitBase.value);
}

function onUpdate(update: ViewUpdate): void {
  if (update.docChanged) {
    revision.value++;
    // Typing in a preview tab keeps it; the agent's changes (remote) don't.
    if (update.transactions.some((tr) => !tr.annotation(Transaction.remote))) {
      canvases.keepFile(props.sessionId, props.path);
    }
    clearTimeout(stripeTimer);
    stripeTimer = setTimeout(refreshStripe, 300);
  }
  if (update.selectionSet || update.focusChanged || update.docChanged || update.geometryChanged) updateChip(update.view);
}

function detach(): void {
  if (attached && attached.view === editor) {
    attached.view = null;
    attached.handlers = {};
  }
  editor?.destroy();
  editor = null;
  attached = null;
}

/**
 * Show the buffer in this canvas's editor. The buffer outlives the canvas (the host keeps only 8
 * tabs alive), so a remount, or a cached canvas reused for a reopened file, attaches again.
 */
async function attach(): Promise<void> {
  const current = await openBuffer(props.sessionId, props.path);
  if (!editorHost.value || !current.state) return;
  if (attached === current && editor && current.view === editor) {
    editor.requestMeasure();
    focusIfPicked();
    return;
  }

  detach();
  editor = new EditorView({ state: current.state, parent: editorHost.value });
  current.view = editor;
  current.handlers = { save: () => void save(), update: onUpdate };
  attached = current;
  revision.value++;
  applyMerge();
  refreshStripe();
  focusIfPicked();
}

/** A file picked in Go to file takes the keyboard straight away. */
function focusIfPicked(): void {
  if (editor && goToFile.takeFocus(props.sessionId, props.path)) editor.focus();
}

/** The merge view the editor should show: Compare while comparing, Diff in Diff, else none. */
function applyMerge(): void {
  const current = record();
  if (!current?.state) return;
  if (comparing.value && conflict.value?.diskText != null) {
    setMerge(current, { kind: "compare", original: baseText(conflict.value.diskText) });
  } else if (view.value === "diff") {
    const original = gitBase.value ?? current.savedDoc;
    setMerge(current, original ? { kind: "diff", original } : null);
  } else {
    setMerge(current, null);
  }
}

watch([view, comparing, () => conflict.value?.hash, gitBase], () => {
  if (attached) applyMerge();
});
watch(gitBase, refreshStripe);
watch(() => info.value?.status, (status) => {
  if (status === "ready") void attach();
});
watch(conflict, (next) => {
  if (!next) comparing.value = false;
});

function mountDeleted(): void {
  if (!deleted.value || !deletedHost.value || deletedView) return;
  deletedView = createDeletedView(deletedHost.value, diffItem.value?.before ?? "");
}
watch(deleted, () => void nextTick(mountDeleted));

onMounted(() => {
  void attach();
  mountDeleted();
});
onActivated(() => void attach());
onDeactivated(() => hideChip());
onBeforeUnmount(() => {
  clearTimeout(stripeTimer);
  clearTimeout(toastTimer);
  detach();
  deletedView?.destroy();
});

// ─── Rendered ────────────────────────────────────────────────────────────────

const renderedContent = computed(() => {
  void revision.value;
  return showRendered.value ? (record()?.state?.doc.toString() ?? "") : "";
});
const renderer = computed(() => getVisualRenderer(isHtml.value ? "html" : "markdown"));

function onAnnotate(anchor: AnnotationAnchor, position: { x: number; y: number }): void {
  annotate(anchor, position, props.path);
}

// ─── Saving and conflicts ────────────────────────────────────────────────────

const toastMessage = ref<string | null>(null);
let toastTimer: ReturnType<typeof setTimeout> | undefined;

function toast(message: string): void {
  toastMessage.value = message;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => {
    toastMessage.value = null;
  }, 2600);
}

async function save(): Promise<void> {
  if (conflict.value) return;
  if (!info.value?.dirty) {
    toast("No changes to save");
    return;
  }
  const outcome = await saveBuffer(props.sessionId, props.path);
  if (outcome.kind === "saved") toast(`Saved ${props.path}`);
  else if (outcome.kind === "error") toast(`Couldn't save: ${outcome.message}`);
}

async function keepMine(): Promise<void> {
  const outcome = await saveBuffer(props.sessionId, props.path, { overwrite: true });
  comparing.value = false;
  if (outcome.kind === "saved") toast(`Saved your version of ${fileName.value}`);
  else if (outcome.kind === "error") toast(`Couldn't save: ${outcome.message}`);
}

function useAgents(): void {
  comparing.value = false;
  useDiskVersion(props.sessionId, props.path);
  refreshStripe();
  editor?.focus();
}

function compare(): void {
  comparing.value = true;
  editor?.focus();
}

function doneComparing(): void {
  finishCompare(props.sessionId, props.path);
  comparing.value = false;
  toast(info.value?.dirty ? "Merged. Save to write it." : "You took the agent's version");
  refreshStripe();
  // Back to the editor, so Ctrl S saves the merge.
  editor?.focus();
}

// ─── Add to message ──────────────────────────────────────────────────────────

const chip = shallowRef<{ label: string; reference: string; x: number; y: number } | null>(null);
let chipBlurTimer: ReturnType<typeof setTimeout> | undefined;

function hideChip(): void {
  chip.value = null;
}

function updateChip(target: EditorView): void {
  const selection = target.state.selection.main;
  if (selection.empty || !target.hasFocus || !root.value) {
    // Blur fires before a click on the chip lands; wait a moment before hiding it.
    clearTimeout(chipBlurTimer);
    chipBlurTimer = setTimeout(() => {
      if (!editor?.hasFocus || editor.state.selection.main.empty) hideChip();
    }, 150);
    return;
  }

  const doc = target.state.doc;
  const first = doc.lineAt(selection.from).number;
  let last = doc.lineAt(selection.to).number;
  // A selection that ends at the start of a line doesn't include that line.
  if (last > first && doc.lineAt(selection.to).from === selection.to) last--;
  const coords = target.coordsAtPos(selection.head);
  if (!coords) {
    hideChip();
    return;
  }

  const box = root.value.getBoundingClientRect();
  const width = 190;
  const x = Math.min(Math.max(8, coords.left - box.left - width / 2), Math.max(8, box.width - width - 8));
  let y = coords.bottom - box.top + 8;
  if (y + 36 > box.height) y = coords.top - box.top - 36;
  chip.value = {
    label: first === last ? `Add line ${first} to message` : `Add lines ${first}–${last} to message`,
    reference: `@${props.path}:${first === last ? first : `${first}-${last}`}`,
    x: Math.round(x),
    y: Math.round(y),
  };
}

function addToMessage(): void {
  if (!chip.value) return;
  appendDraftReference(props.sessionId, chip.value.reference);
  hideChip();
  dispatchCommandEvent("weave:command-focus-prompt", { sessionId: props.sessionId });
}
</script>

<template>
  <div
    ref="root"
    class="file-canvas"
    :class="{ 'file-canvas--merge': comparing || view === 'diff' }"
    :data-path="path"
  >
    <div class="file-canvas__bar">
      <span
        class="file-canvas__crumbs"
        :title="path"
      >
        <template
          v-for="(folder, index) in folders"
          :key="index"
        >
          <span class="file-canvas__crumb-folder">{{ folder }}</span>
          <ChevronRight
            :size="11"
            class="file-canvas__crumb-sep"
            aria-hidden="true"
          />
        </template>
        <b class="file-canvas__crumb-file">{{ fileName }}</b>
      </span>
      <span class="file-canvas__spacer" />

      <span
        v-if="conflict"
        class="file-canvas__state"
        data-testid="file-state"
      >
        <span class="file-canvas__state-dot file-canvas__state-dot--conflict" />Changed on disk
      </span>
      <span
        v-else-if="info?.saving"
        class="file-canvas__state"
        data-testid="file-state"
      >Saving…</span>
      <span
        v-else-if="info?.dirty"
        class="file-canvas__state"
        data-testid="file-state"
      >
        <span class="file-canvas__state-dot" />Unsaved
        <button
          type="button"
          class="file-canvas__save"
          data-testid="file-save"
          @click="save"
        >Save<kbd>{{ saveKey }}</kbd></button>
      </span>

      <div
        v-if="ready"
        class="file-canvas__toggle"
        role="group"
        aria-label="View"
      >
        <button
          v-if="renderable"
          type="button"
          :class="{ 'is-on': view === 'rendered' }"
          :aria-pressed="view === 'rendered'"
          data-testid="file-view-rendered"
          @click="setView('rendered')"
        >
          Rendered
        </button>
        <button
          type="button"
          :class="{ 'is-on': view === 'edit' }"
          :aria-pressed="view === 'edit'"
          data-testid="file-view-edit"
          @click="setView('edit')"
        >
          {{ renderable ? "Source" : "Edit" }}
        </button>
        <button
          type="button"
          :class="{ 'is-on': view === 'diff' }"
          :aria-pressed="view === 'diff'"
          :disabled="!hasChanges"
          :title="hasChanges ? 'Diff against the last commit' : 'No changes in this file'"
          data-testid="file-view-diff"
          @click="setView('diff')"
        >
          Diff
        </button>
      </div>
    </div>

    <div
      v-if="conflict && !comparing"
      class="file-canvas__notice"
      role="alert"
      data-testid="file-conflict"
    >
      <span v-if="conflict.diskText !== null">
        <b>The agent changed {{ fileName }} while you were editing.</b> Nothing was overwritten.
      </span>
      <span v-else>
        <b>{{ fileName }} changed on disk and can't be shown here any more.</b> Nothing was overwritten.
      </span>
      <span class="file-canvas__notice-actions">
        <button
          v-if="conflict.diskText !== null"
          type="button"
          data-testid="conflict-compare"
          @click="compare"
        >Compare</button>
        <button
          type="button"
          data-testid="conflict-mine"
          @click="keepMine"
        >Keep mine</button>
        <button
          v-if="conflict.diskText !== null"
          type="button"
          data-testid="conflict-theirs"
          @click="useAgents"
        >Use the agent's</button>
      </span>
    </div>
    <div
      v-else-if="comparing"
      class="file-canvas__notice file-canvas__notice--info"
      role="status"
      data-testid="file-comparing"
    >
      <span><b>Red is the agent's version, green is yours.</b> Take the agent's lines hunk by hunk, then press Done and save.</span>
      <span class="file-canvas__notice-actions">
        <button
          type="button"
          class="is-primary"
          data-testid="compare-done"
          @click="doneComparing"
        >Done</button>
      </span>
    </div>

    <p
      v-if="info?.status === 'loading'"
      class="file-canvas__note"
    >
      Opening {{ path }}…
    </p>
    <template v-else-if="deleted">
      <p
        class="file-canvas__note"
        role="status"
      >
        This file was deleted in this session. Its last version:
      </p>
      <div
        ref="deletedHost"
        class="file-canvas__editor"
      />
    </template>
    <p
      v-else-if="info?.status === 'unavailable' || info?.status === 'error'"
      class="file-canvas__note"
      role="status"
      data-testid="file-unavailable"
    >
      {{ info.message }}
    </p>

    <div
      v-if="showRendered && renderer"
      class="file-canvas__rendered"
      :class="{ 'file-canvas__rendered--html': isHtml }"
      data-testid="file-rendered"
    >
      <component
        :is="renderer"
        :content="renderedContent"
        :annotatable="!isHtml"
        @annotate="onAnnotate"
      />
    </div>
    <div
      v-show="showEditor"
      ref="editorHost"
      class="file-canvas__editor"
      data-testid="file-editor"
      @scroll.capture="hideChip"
    />

    <button
      v-if="chip && showEditor"
      type="button"
      class="file-canvas__chip"
      :style="{ left: `${chip.x}px`, top: `${chip.y}px` }"
      data-testid="add-to-message"
      @mousedown.prevent
      @click="addToMessage"
    >
      <MessageSquarePlus
        :size="13"
        aria-hidden="true"
      />
      {{ chip.label }}
    </button>

    <div
      class="file-canvas__toast"
      :class="{ 'is-shown': toastMessage }"
      role="status"
      aria-live="polite"
    >
      {{ toastMessage }}
    </div>
  </div>
</template>

<style scoped>
.file-canvas {
  position: relative;
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
  container-type: inline-size;
}

.file-canvas__bar {
  display: flex;
  align-items: center;
  gap: 8px;
  min-height: 38px;
  padding: 0 10px 0 14px;
  border-bottom: 1px solid var(--border);
}

.file-canvas__crumbs {
  display: flex;
  align-items: center;
  gap: 3px;
  min-width: 0;
  overflow: hidden;
  font-size: 12.5px;
  color: var(--muted);
  white-space: nowrap;
}

.file-canvas__crumb-file {
  overflow: hidden;
  text-overflow: ellipsis;
  font-weight: 500;
  color: var(--text);
}

.file-canvas__crumb-sep {
  flex-shrink: 0;
}

.file-canvas__spacer {
  flex: 1;
}

.file-canvas__state {
  display: inline-flex;
  flex-shrink: 0;
  align-items: center;
  gap: 8px;
  font-size: 12px;
  color: var(--muted);
  white-space: nowrap;
}

.file-canvas__state-dot {
  width: 7px;
  height: 7px;
  border-radius: 50%;
  background: var(--idle);
}

.file-canvas__state-dot--conflict {
  background: var(--error);
}

.file-canvas__save,
.file-canvas__notice-actions button {
  height: 24px;
  padding: 0 9px;
  border: 1px solid var(--border);
  border-radius: 7px;
  background: var(--card-bg);
  color: var(--text);
  font-size: 12px;
  font-weight: 500;
  cursor: pointer;
  transition: border-color var(--transition);
}

.file-canvas__save:hover,
.file-canvas__notice-actions button:hover {
  border-color: var(--accent);
}

.file-canvas__save kbd {
  margin-left: 5px;
  font: 500 10.5px var(--font-sans-stack);
  color: var(--muted);
}

.file-canvas__toggle {
  display: inline-flex;
  flex-shrink: 0;
  gap: 1px;
  padding: 2px;
  border-radius: 8px;
  background: color-mix(in srgb, var(--text) 5%, transparent);
}

.file-canvas__toggle button {
  height: 22px;
  padding: 0 8px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--muted);
  font-size: 12px;
  cursor: pointer;
  transition: background-color var(--transition), color var(--transition);
}

.file-canvas__toggle button.is-on {
  background: color-mix(in srgb, var(--text) 10%, transparent);
  color: var(--text);
}

.file-canvas__toggle button:disabled {
  opacity: 0.45;
  cursor: not-allowed;
}

.file-canvas__toggle button:focus-visible,
.file-canvas__save:focus-visible,
.file-canvas__notice-actions button:focus-visible,
.file-canvas__chip:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 1px;
}

.file-canvas__notice {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px 12px;
  padding: 8px 12px;
  border-bottom: 1px solid var(--border);
  background: color-mix(in srgb, var(--idle) 14%, transparent);
  font-size: 12.5px;
  line-height: 1.45;
  color: var(--text);
  animation: file-canvas-rise 240ms ease-out;
}

.file-canvas__notice--info {
  background: var(--accent-dim);
}

.file-canvas__notice b {
  font-weight: 600;
}

.file-canvas__notice-actions {
  display: flex;
  gap: 6px;
  margin-left: auto;
}

.file-canvas__notice-actions button.is-primary {
  border-color: var(--accent);
  background: var(--accent);
  color: var(--primary-foreground);
}

.file-canvas__editor {
  flex: 1;
  min-height: 0;
  overflow: hidden;
}

.file-canvas__editor :deep(.cm-editor) {
  height: 100%;
}

/* The merge view has its own change gutter; the stripe would repeat it. */
.file-canvas--merge .file-canvas__editor :deep(.cm-agent-gutter) {
  display: none;
}

.file-canvas__rendered {
  flex: 1;
  min-height: 0;
  overflow: auto;
  padding: 16px 22px 28px;
}

.file-canvas__rendered--html {
  padding: 0;
  overflow: hidden;
}

.file-canvas__note {
  margin: 0;
  padding: 14px 18px;
  font-size: 13px;
  color: var(--muted);
}

.file-canvas__chip {
  position: absolute;
  top: 0;
  left: 0;
  z-index: 5;
  display: inline-flex;
  align-items: center;
  gap: 6px;
  height: 28px;
  padding: 0 10px;
  border: 1px solid var(--border);
  border-radius: 8px;
  background: var(--card-bg);
  color: var(--text);
  font-size: 12px;
  font-weight: 500;
  white-space: nowrap;
  box-shadow: 0 16px 40px -12px rgba(0, 0, 0, 0.45);
  cursor: pointer;
  animation: file-canvas-pop 140ms ease-out;
}

.file-canvas__chip:hover {
  border-color: var(--accent);
}

.file-canvas__chip svg {
  color: var(--accent);
}

.file-canvas__toast {
  position: absolute;
  bottom: 18px;
  left: 50%;
  z-index: 20;
  max-width: calc(100% - 32px);
  padding: 7px 12px;
  border-radius: 9px;
  background: var(--text);
  color: var(--panel-bg);
  font-size: 12.5px;
  font-weight: 500;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  opacity: 0;
  pointer-events: none;
  transform: translate(-50%, 8px);
  transition: opacity 160ms ease-out, transform 160ms ease-out;
}

.file-canvas__toast.is-shown {
  opacity: 1;
  transform: translate(-50%, 0);
}

/* A narrow panel or the phone sheet: the file name is enough. */
@container (max-width: 430px) {
  .file-canvas__crumb-folder,
  .file-canvas__crumb-sep {
    display: none;
  }

  .file-canvas__save kbd {
    display: none;
  }
}

@keyframes file-canvas-rise {
  from {
    opacity: 0;
    transform: translateY(-4px);
  }
}

@keyframes file-canvas-pop {
  from {
    opacity: 0;
    transform: scale(0.96);
  }
}

@media (prefers-reduced-motion: reduce) {
  .file-canvas__notice,
  .file-canvas__chip {
    animation: none;
  }

  .file-canvas__toast,
  .file-canvas__toggle button,
  .file-canvas__save,
  .file-canvas__notice-actions button {
    transition: none;
  }
}
</style>
