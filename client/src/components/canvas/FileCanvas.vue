<script setup lang="ts">
import { computed, inject, nextTick, onActivated, onBeforeUnmount, onMounted, ref, shallowRef, watch } from "vue";
import { ChevronRight, Link, MessageSquarePlus, Quote, WrapText, X } from "lucide-vue-next";
import { Transaction, type EditorState, type Text } from "@codemirror/state";
import { EditorView, type ViewUpdate } from "@codemirror/view";
import { useDiffBase } from "@/composables/use-diff-base";
import { baseKey, type UseDiffsResult } from "@/composables/use-diffs";
import { useCanvasAnnotate } from "@/composables/use-canvas-annotation";
import { useSessionImage } from "@/composables/use-session-image";
import { appendDraftReference } from "@/composables/use-draft-state";
import type { AnnotationAnchor } from "@/lib/annotation-types";
import { runSessionCommand } from "@/lib/session-commands";
import { baseText } from "@/lib/code-editor/agent-lines";
import { finishCompare, openBuffer, saveBuffer, useDiskVersion } from "@/lib/code-editor/buffers";
import { isWrapped, setWrap } from "@/lib/code-editor/editor-extensions";
import { clearPick, pickedLines, selectedLines, type LineRange } from "@/lib/code-editor/line-pick";
import { WRAP_BY_DEFAULT } from "@/lib/code-editor/wrap-preference";
import { useMachineTarget } from "@/lib/machine-target";
import { createDeletedView, markStripe, setMerge } from "@/lib/code-editor/merge";
import { getVisualRenderer } from "@/lib/visual-renderer-registry";
import { hasRenderedView, isImagePath, useCanvasesStore, type FileView } from "@/stores/canvases";
import { useFileBuffersStore, type FileBufferRecord } from "@/stores/file-buffers";
import { useGoToFileStore } from "@/stores/go-to-file";

const props = defineProps<{
  sessionId: string;
  path: string;
  view: FileView;
}>();

const machine = useMachineTarget();

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
const wrapKey = isMac ? "⌥Z" : "Alt Z";

// ─── What's shown ────────────────────────────────────────────────────────────

const renderable = computed(() => hasRenderedView(props.path));
const isHtml = computed(() => /\.html?$/i.test(props.path));
const isImage = computed(() => isImagePath(props.path));
const diffItem = computed(() => sharedDiffs?.byFile.value.get(props.path) ?? null);
const inDiff = computed(() => diffItem.value !== null && !isImage.value);
// The diff list has no contents; the base is fetched once, when the file is among the changes.
const diffBase = useDiffBase(() => props.sessionId, () => props.path, inDiff, () => baseKey(sharedDiffs?.base.value));
const gitBase = computed<Text | null>(() => (inDiff.value && diffBase.value !== null ? baseText(diffBase.value) : null));
const hasChanges = computed(() => inDiff.value || (info.value?.dirty ?? false));
const deleted = computed(() =>
  diffItem.value?.status === "deleted" && info.value?.status === "error" && diffBase.value !== null);

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
  if (current) markStripe(current, gitBase.value, comparing.value || view.value === "diff");
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
  syncFromState(update.state);
}

/** What the bar shows that lives in the editor's state: wrap, and the picked or selected lines. */
function syncFromState(state: EditorState): void {
  wrap.value = isWrapped(state);
  lines.value = sameLines(lines.value, pickedLines(state) ?? selectedLines(state));
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
  if (isImage.value) return;
  const current = await openBuffer(machine, props.sessionId, props.path);
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
  syncFromState(editor.state);
  applyMerge();
  refreshStripe();
  focusIfPicked();
}

/** A file picked in Go to file, or opened at a line from a message, takes the keyboard straight away. */
function focusIfPicked(): void {
  const picked = editor ? goToFile.takeFocus(props.sessionId, props.path) : null;
  if (!editor || !picked) return;
  if (picked.line) {
    const doc = editor.state.doc;
    const line = doc.line(Math.min(picked.line, doc.lines));
    editor.dispatch({ selection: { anchor: line.from }, effects: EditorView.scrollIntoView(line.from, { y: "center" }) });
  }
  editor.focus();
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
  if (!attached) return;
  applyMerge();
  refreshStripe();
});
watch(gitBase, refreshStripe);
// A file that's open already, opened again at a line. After the render, so a kept-alive tab is back on the page.
watch(() => goToFile.focusOnOpen, focusIfPicked, { flush: "post" });
watch(() => info.value?.status, (status) => {
  if (status === "ready") void attach();
});
watch(conflict, (next) => {
  if (!next) comparing.value = false;
});

function mountDeleted(): void {
  if (!deleted.value || !deletedHost.value || deletedView) return;
  deletedView = createDeletedView(deletedHost.value, diffBase.value ?? "");
}
watch(deleted, () => void nextTick(mountDeleted));

onMounted(() => {
  void attach();
  mountDeleted();
});
onActivated(() => {
  void attach();
  // The agent may have changed the picture while the tab was in the back.
  if (image.src.value) image.reload();
});
onBeforeUnmount(() => {
  clearTimeout(stripeTimer);
  clearTimeout(toastTimer);
  detach();
  deletedView?.destroy();
});

// ─── Images ──────────────────────────────────────────────────────────────────

// An image is only ever shown, never edited, at any size.
const image = useSessionImage(() => props.sessionId, () => props.path, isImage);
const imageDims = ref<{ width: number; height: number } | null>(null);
const imageBroken = ref(false);
const imageNote = computed(() => image.error.value ?? (imageBroken.value ? "This image couldn't be shown." : null));
const imageMeta = computed(() => {
  const bytes = image.bytes.value;
  if (bytes === null || !image.src.value) return null;
  const size = bytes >= 1_048_576 ? `${(bytes / 1_048_576).toFixed(1)} MB`
    : bytes >= 1024 ? `${Math.round(bytes / 1024)} KB`
    : `${bytes} B`;
  // An SVG without a width and height has no pixel size of its own.
  return imageDims.value ? `${imageDims.value.width} × ${imageDims.value.height} · ${size}` : size;
});

watch(() => image.src.value, () => {
  imageBroken.value = false;
});
watch(() => props.path, () => {
  imageDims.value = null;
});

function onImageLoad(event: Event): void {
  const img = event.target as HTMLImageElement;
  imageDims.value = img.naturalWidth > 0 ? { width: img.naturalWidth, height: img.naturalHeight } : null;
}

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
  const outcome = await saveBuffer(machine, props.sessionId, props.path);
  if (outcome.kind === "saved") toast(`Saved ${props.path}`);
  else if (outcome.kind === "error") toast(`Couldn't save: ${outcome.message}`);
}

async function keepMine(): Promise<void> {
  const outcome = await saveBuffer(machine, props.sessionId, props.path, { overwrite: true });
  comparing.value = false;
  if (outcome.kind === "saved") toast(`Saved your version of ${fileName.value}`);
  else if (outcome.kind === "error") toast(`Couldn't save: ${outcome.message}`);
}

function useAgents(): void {
  comparing.value = false;
  useDiskVersion(machine, props.sessionId, props.path);
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

// ─── Wrap ────────────────────────────────────────────────────────────────────

const wrap = ref(WRAP_BY_DEFAULT);

function toggleWrap(): void {
  if (editor) setWrap(editor, props.path, !wrap.value);
}

// ─── Picked lines: Copy link, Quote, Add to message ──────────────────────────

const lines = shallowRef<LineRange | null>(null);

function sameLines(current: LineRange | null, next: LineRange | null): LineRange | null {
  return current && next && current.first === next.first && current.last === next.last ? current : next;
}

const linesLabel = computed(() => {
  const range = lines.value;
  if (!range) return "";
  return range.first === range.last ? `L${range.first}` : `L${range.first}–${range.last}`;
});
const linesCount = computed(() => {
  const count = lines.value ? lines.value.last - lines.value.first + 1 : 0;
  return count === 1 ? "1 line" : `${count} lines`;
});
const linesReference = computed(() => {
  const range = lines.value;
  if (!range) return props.path;
  return `${props.path}:${range.first === range.last ? range.first : `${range.first}-${range.last}`}`;
});

/** Clear the picked lines, or the text selection the bar was showing. */
function clearLines(): void {
  if (!editor || clearPick(editor)) return;
  const selection = editor.state.selection.main;
  if (!selection.empty) editor.dispatch({ selection: { anchor: selection.head } });
}

async function copy(text: string, done: string): Promise<void> {
  try {
    await navigator.clipboard.writeText(text);
    toast(done);
  } catch {
    toast("Couldn't copy to the clipboard");
  }
}

function copyLink(): void {
  void copy(linesReference.value, `Copied ${linesReference.value}`);
}

/** The reference, then the lines in a fence that's longer than any run of backticks inside them. */
function copyQuote(): void {
  const range = lines.value;
  if (!editor || !range) return;
  const doc = editor.state.doc;
  const text = doc.sliceString(doc.line(range.first).from, doc.line(range.last).to);
  const longest = Math.max(0, ...(text.match(/`+/g) ?? []).map((run) => run.length));
  const fence = "`".repeat(Math.max(3, longest + 1));
  const extension = /\.([a-z0-9]+)$/i.exec(fileName.value)?.[1]?.toLowerCase() ?? "";
  void copy(`${linesReference.value}\n${fence}${extension}\n${text}\n${fence}\n`, `Copied ${linesLabel.value} as a quote`);
}

function addToMessage(): void {
  if (!lines.value) return;
  appendDraftReference(props.sessionId, `@${linesReference.value}`);
  clearLines();
  runSessionCommand("focus-prompt", props.sessionId);
}
</script>

<template>
  <div
    ref="root"
    class="file-canvas"
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
        v-if="imageMeta"
        class="file-canvas__state"
        data-testid="image-meta"
      >{{ imageMeta }}</span>

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

      <button
        v-if="showEditor"
        type="button"
        class="file-canvas__wrap"
        :class="{ 'is-on': wrap }"
        :aria-pressed="wrap"
        aria-label="Wrap"
        :title="`Wrap long lines (${wrapKey})`"
        data-testid="file-wrap"
        @click="toggleWrap"
      >
        <WrapText
          :size="14"
          aria-hidden="true"
        />
        <span class="file-canvas__wrap-label">Wrap</span>
      </button>

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

    <template v-if="isImage">
      <p
        v-if="imageNote"
        class="file-canvas__note"
        role="status"
        data-testid="file-unavailable"
      >
        {{ imageNote }}
      </p>
      <div
        v-else-if="image.src.value"
        class="file-canvas__image"
        data-testid="file-image"
      >
        <img
          :src="image.src.value"
          :alt="fileName"
          @load="onImageLoad"
          @error="imageBroken = true"
        >
      </div>
      <p
        v-else
        class="file-canvas__note"
      >
        Opening {{ path }}…
      </p>
    </template>
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
    />

    <div
      v-if="lines && showEditor"
      class="file-canvas__lines"
      role="toolbar"
      aria-label="Picked lines"
      data-testid="line-bar"
    >
      <span class="file-canvas__lines-label">
        <b data-testid="line-bar-label">{{ linesLabel }}</b>
        <span>{{ linesCount }}</span>
      </span>
      <span class="file-canvas__spacer" />
      <button
        type="button"
        aria-label="Copy link"
        :title="`Copy ${linesReference}`"
        data-testid="copy-link"
        @mousedown.prevent
        @click="copyLink"
      >
        <Link
          :size="13"
          aria-hidden="true"
        />
        <span class="file-canvas__lines-text">Copy link</span>
      </button>
      <button
        type="button"
        aria-label="Quote"
        title="Copy the lines as a quote"
        data-testid="copy-quote"
        @mousedown.prevent
        @click="copyQuote"
      >
        <Quote
          :size="13"
          aria-hidden="true"
        />
        <span class="file-canvas__lines-text">Quote</span>
      </button>
      <button
        type="button"
        class="is-primary"
        data-testid="add-to-message"
        @mousedown.prevent
        @click="addToMessage"
      >
        <MessageSquarePlus
          :size="13"
          aria-hidden="true"
        />
        Add to message
      </button>
      <button
        type="button"
        class="file-canvas__lines-clear"
        aria-label="Clear"
        title="Clear (Esc)"
        data-testid="line-bar-clear"
        @mousedown.prevent
        @click="clearLines"
      >
        <X
          :size="13"
          aria-hidden="true"
        />
      </button>
    </div>

    <div
      class="file-canvas__toast"
      :class="{ 'is-shown': toastMessage, 'is-lifted': lines && showEditor }"
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
.file-canvas__wrap:focus-visible,
.file-canvas__save:focus-visible,
.file-canvas__notice-actions button:focus-visible,
.file-canvas__lines button:focus-visible {
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

.file-canvas__image {
  flex: 1;
  min-height: 0;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 24px;
  overflow: hidden;
}

/* A checkerboard behind the picture, so transparent parts read as transparent in either theme. */
.file-canvas__image img {
  max-width: 100%;
  max-height: 100%;
  object-fit: contain;
  background: repeating-conic-gradient(var(--border) 0 25%, transparent 0 50%) 0 0 / 16px 16px;
  box-shadow: 0 0 0 1px var(--border);
}

/* Picked lines, or the lines a selection spans. Docked, so scrolling the file never hides it. */
.file-canvas__lines {
  display: flex;
  flex-shrink: 0;
  align-items: center;
  gap: 4px;
  margin: 0 8px 8px;
  padding: 5px 6px 5px 12px;
  border: 1px solid var(--border);
  border-radius: 10px;
  background: var(--card-bg);
  box-shadow: 0 16px 40px -12px rgba(0, 0, 0, 0.45);
  font-size: 12px;
  white-space: nowrap;
  animation: file-canvas-pop 140ms ease-out;
}

.file-canvas__lines-label {
  display: inline-flex;
  align-items: baseline;
  gap: 6px;
  color: var(--muted);
  font-size: 11px;
}

.file-canvas__lines-label b {
  font-family: var(--font-mono-stack);
  font-size: 12px;
  font-weight: 600;
  color: var(--text);
}

.file-canvas__lines button {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  height: 26px;
  padding: 0 8px;
  border: 1px solid var(--border);
  border-radius: 7px;
  background: transparent;
  color: var(--text);
  font-size: 12px;
  font-weight: 500;
  cursor: pointer;
  transition: border-color var(--transition), background-color var(--transition);
}

.file-canvas__lines button:hover {
  border-color: var(--accent);
}

.file-canvas__lines button.is-primary {
  border-color: var(--accent);
  background: var(--accent);
  color: var(--primary-foreground);
}

.file-canvas__lines button.file-canvas__lines-clear {
  padding: 0 6px;
  border-color: transparent;
  color: var(--muted);
}

.file-canvas__lines button.file-canvas__lines-clear:hover {
  color: var(--text);
}

.file-canvas__wrap {
  display: inline-flex;
  flex-shrink: 0;
  align-items: center;
  gap: 5px;
  height: 26px;
  padding: 0 8px;
  border: 0;
  border-radius: 7px;
  background: transparent;
  color: var(--muted);
  font-size: 12px;
  cursor: pointer;
  transition: background-color var(--transition), color var(--transition);
}

.file-canvas__wrap:hover {
  color: var(--text);
}

.file-canvas__wrap.is-on {
  background: var(--accent-dim);
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

.file-canvas__toast.is-lifted {
  bottom: 58px;
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

  .file-canvas__save kbd,
  .file-canvas__wrap-label {
    display: none;
  }

  .file-canvas__wrap {
    padding: 0 6px;
  }
}

@container (max-width: 420px) {
  .file-canvas__lines-text {
    display: none;
  }

  .file-canvas__lines button {
    padding: 0 6px;
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
  .file-canvas__lines {
    animation: none;
  }

  .file-canvas__toast,
  .file-canvas__wrap,
  .file-canvas__lines button,
  .file-canvas__toggle button,
  .file-canvas__save,
  .file-canvas__notice-actions button {
    transition: none;
  }
}
</style>
