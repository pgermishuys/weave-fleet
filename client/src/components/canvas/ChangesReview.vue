<script setup lang="ts">
import { computed, inject, nextTick, onBeforeUnmount, ref, shallowRef, watch } from "vue";
import { useElementSize, useEventListener } from "@vueuse/core";
import {
  ChevronDown,
  ChevronsDownUp,
  ChevronsUpDown,
  Copy,
  Ellipsis,
  ExternalLink,
  FileText,
  Search,
  Undo2,
  WrapText,
} from "lucide-vue-next";
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import { readSessionFile, restoreSessionFile, searchSessionChanges, writeSessionFile } from "@/api/session-files";
import type { FileDiffItem } from "@/api/client";
import LinesBar from "@/components/canvas/LinesBar.vue";
import { useCanvasAnnotate } from "@/composables/use-canvas-annotation";
import { appendDraftReference, appendDraftText } from "@/composables/use-draft-state";
import type { UseDiffsResult } from "@/composables/use-diffs";
import type { AnnotationAnchor } from "@/lib/annotation-types";
import { useFileDiffContents } from "@/composables/use-file-diff-contents";
import { diffBaseLabel } from "@/lib/diff-base";
import { useMachineTarget } from "@/lib/machine-target";
import { parseDiffLines, type DiffLine } from "@/lib/diff-parser";
import {
  buildReview,
  markedSegments,
  splitRows,
  textWithRangeReverted,
  type ReviewHunk,
  type ReviewItem,
  type ReviewLine,
} from "@/lib/diff-review";
import { lineCountLabel, lineQuote, lineRangeLabel, lineReference, sliceLines } from "@/lib/line-quote";
import { sameSelection, selectionLines, type ReviewSelection, type SelectedRow } from "@/lib/review-selection";
import { runSessionCommand } from "@/lib/session-commands";
import { getVisualRenderer } from "@/lib/visual-renderer-registry";
import { findLines, type ChangesHit, type FindFile } from "@/lib/changes-find";
import { useChangesFind } from "@/composables/use-changes-find";
import ChangesFindBar from "@/components/canvas/ChangesFindBar.vue";
import { hasRenderedView, useCanvasesStore } from "@/stores/canvases";

const props = defineProps<{ sessionId: string }>();

const SPLIT_FROM_WIDTH = 1100;
const LAYOUT_KEY = "weave:changes-layout";
const WRAP_KEY = "weave:changes-wrap";

const machine = useMachineTarget();
const canvases = useCanvasesStore();
const sharedDiffs = inject<UseDiffsResult | null>("sharedDiffs", null);
const { entries, load, reload } = useFileDiffContents(machine, () => props.sessionId);

// ─── Header: scope, layout, wrap ─────────────────────────────────────────────

const root = ref<HTMLElement | null>(null);
const body = ref<HTMLElement | null>(null);
const { width: panelWidth } = useElementSize(root);

function readPref<T extends string>(key: string, allowed: readonly T[]): T | null {
  try {
    const value = window.localStorage.getItem(key);
    return allowed.includes(value as T) ? (value as T) : null;
  } catch {
    return null;
  }
}
function writePref(key: string, value: string): void {
  try {
    window.localStorage.setItem(key, value);
  } catch {
    // localStorage unavailable
  }
}

const layoutPref = shallowRef<"unified" | "split" | null>(readPref(LAYOUT_KEY, ["unified", "split"] as const));
// Unified below 1100px of panel, split above, until the user picks one.
const layout = computed<"unified" | "split">(() =>
  layoutPref.value ?? (panelWidth.value >= SPLIT_FROM_WIDTH ? "split" : "unified"),
);
function setLayout(next: "unified" | "split"): void {
  layoutPref.value = next;
  writePref(LAYOUT_KEY, next);
}

const wrap = shallowRef(readPref(WRAP_KEY, ["on", "off"] as const) === "on");
// Opens the file in a file tab (Diff for a changed file, Edit for a deleted-in-diff one isn't openable).
function openInEditor(file: { file: string }): void {
  canvases.openFile(props.sessionId, file.file, { keep: true, view: "diff" });
}
function toggleWrap(): void {
  wrap.value = !wrap.value;
  writePref(WRAP_KEY, wrap.value ? "on" : "off");
}

const baseDetail = computed(() => {
  const label = diffBaseLabel(sharedDiffs?.base.value ?? null);
  return label ? `${label.text}${label.commit ? ` ${label.commit}` : ""}` : "Which changes to show";
});

const allFiles = computed(() =>
  [...(sharedDiffs?.diffs.value ?? [])].sort((a, b) => a.file.localeCompare(b.file)),
);
// The whole session, always: a "This turn" scope waits for a per-turn git baseline (tool-written files alone miss
// shell deletes, renames and binaries).
const files = allFiles;
const totals = computed(() => ({
  additions: files.value.reduce((sum, file) => sum + file.additions, 0),
  deletions: files.value.reduce((sum, file) => sum + file.deletions, 0),
}));

// ─── Files: collapse, contents ───────────────────────────────────────────────

const AUTO_OPEN = 8;
const BIG_DIFF_LINES = 1500;
const GENERATED_FILE =
  /(^|\/)(package-lock\.json|npm-shrinkwrap\.json|yarn\.lock|pnpm-lock\.yaml|bun\.lockb?|Cargo\.lock|Gemfile\.lock|poetry\.lock|uv\.lock|composer\.lock|go\.sum|packages\.lock\.json|Pipfile\.lock|flake\.lock)$|\.(min\.js|min\.css|lock|map)$/i;
function startsCollapsed(file: FileDiffItem): boolean {
  return GENERATED_FILE.test(file.file) || file.additions + file.deletions > BIG_DIFF_LINES;
}
const collapsed = shallowRef<ReadonlySet<string>>(new Set());
const expandedAtStart = shallowRef(false);
watch(
  files,
  (list) => {
    if (expandedAtStart.value || list.length === 0) return;
    expandedAtStart.value = true;
    // A long list opens its first few files; the rest are one click away. Lockfiles and other generated or very large
    // diffs start shut whatever their place: nobody reads them, and drawing thousands of lines freezes the panel.
    const shut = list.filter((file, index) => index >= AUTO_OPEN || startsCollapsed(file));
    collapsed.value = new Set(shut.map((file) => file.file));
  },
  { immediate: true },
);

function isOpen(path: string): boolean {
  return !collapsed.value.has(path);
}
function toggleFile(path: string): void {
  const next = new Set(collapsed.value);
  if (!next.delete(path)) next.add(path);
  collapsed.value = next;
}
const allCollapsed = computed(() => files.value.length > 0 && files.value.every((file) => !isOpen(file.file)));
function toggleAll(): void {
  collapsed.value = allCollapsed.value ? new Set() : new Set(files.value.map((file) => file.file));
}

function versionOf(file: FileDiffItem): string {
  return `${file.status}:${file.additions}:${file.deletions}`;
}

watch(
  [files, collapsed],
  () => {
    for (const file of files.value) {
      if (isOpen(file.file)) load(file.file, versionOf(file));
    }
  },
  { immediate: true },
);

interface FileView {
  item: FileDiffItem;
  entry: (typeof entries.value)[string] | undefined;
  placeholder: string | null;
  diffLines: DiffLine[];
  items: ReturnType<typeof buildReview>;
  trailingNewline: boolean;
}

// A file's lines are worked out once per loaded entry: another file loading doesn't redo them.
const viewCache = new Map<string, { entry: unknown; version: string; view: FileView }>();
const views = computed<Record<string, FileView>>(() => {
  const out: Record<string, FileView> = {};
  for (const file of files.value) {
    const entry = entries.value[file.file];
    const cached = viewCache.get(file.file);
    if (cached && cached.entry === entry && cached.version === versionOf(file)) {
      out[file.file] = cached.view;
      continue;
    }
    let placeholder: string | null = null;
    let diffLines: DiffLine[] = [];
    let trailingNewline = true;
    if (entry?.status === "ready") {
      const item = entry.item;
      if (item.isBinary || item.binary) placeholder = "Binary file — no diff available";
      else if (item.isTruncated || item.truncated) placeholder = "File too large to diff";
      else {
        diffLines = parseDiffLines(item.before ?? "", item.after ?? "");
        trailingNewline = (item.after ?? "").endsWith("\n") || (item.after ?? "") === "";
      }
    }
    const view: FileView = {
      item: file,
      entry,
      placeholder,
      diffLines,
      items: buildReview(diffLines),
      trailingNewline,
    };
    viewCache.set(file.file, { entry, version: versionOf(file), view });
    out[file.file] = view;
  }
  return out;
});

// A diff with thousands of changed lines draws its first hunks; the rest is one click away, so the panel stays usable.
const MAX_DRAWN_LINES = 600;
const drawnAll = shallowRef<ReadonlySet<string>>(new Set());
function showAll(path: string): void {
  drawnAll.value = new Set([...drawnAll.value, path]);
}
function visibleItems(path: string): ReviewItem[] {
  const items = views.value[path]?.items ?? [];
  if (drawnAll.value.has(path)) return items;
  let drawn = 0;
  const out: ReviewItem[] = [];
  for (const item of items) {
    if (drawn >= MAX_DRAWN_LINES) break;
    if (item.kind === "hunk") {
      // One hunk can be the whole file (a change every few lines): cut it too.
      out.push(item.lines.length > MAX_DRAWN_LINES - drawn ? { ...item, lines: item.lines.slice(0, MAX_DRAWN_LINES - drawn) } : item);
      drawn += item.lines.length;
    } else out.push(item);
  }
  return out;
}
function hiddenChanges(path: string): number {
  if (drawnAll.value.has(path)) return 0;
  const total = (views.value[path]?.items ?? []).reduce((sum, item) => (item.kind === "hunk" ? sum + item.lines.length : sum), 0);
  const drawn = visibleItems(path).reduce((sum, item) => (item.kind === "hunk" ? sum + item.lines.length : sum), 0);
  return total - drawn;
}

// Unchanged runs the reader opened, by file and run id.
const openFolds = shallowRef<ReadonlySet<string>>(new Set());
function foldKey(path: string, id: string): string {
  return `${path}\u0000${id}`;
}
function openFold(path: string, id: string): void {
  openFolds.value = new Set([...openFolds.value, foldKey(path, id)]);
}

// ─── Rendered (markdown and html) ────────────────────────────────────────────

const rendered = shallowRef<ReadonlySet<string>>(new Set());
function toggleRendered(path: string, on: boolean): void {
  const next = new Set(rendered.value);
  if (on) next.add(path);
  else next.delete(path);
  rendered.value = next;
}
function rendererFor(path: string) {
  return getVisualRenderer(/\.html?$/i.test(path) ? "html" : "markdown");
}

// ─── Splitting a file's lines into rows ──────────────────────────────────────

function hunkRows(hunk: ReviewHunk) {
  return splitRows(hunk.lines);
}
function foldLines(path: string, fold: { id: string; lines: ReviewLine[] }): ReviewLine[] {
  return openFolds.value.has(foldKey(path, fold.id)) ? fold.lines : [];
}
function splitFoldRows(lines: readonly ReviewLine[]) {
  return splitRows(lines);
}

function segments(line: ReviewLine) {
  return markedSegments(line.content, line.marks);
}

// ─── Revert ──────────────────────────────────────────────────────────────────

interface PendingRevert {
  path: string;
  hunk: ReviewHunk | null;
  title: string;
  detail: string;
}
const pending = shallowRef<PendingRevert | null>(null);
const confirmOpen = computed({
  get: () => pending.value !== null,
  // The dialog reports itself closed before the Revert button's click handler runs; clearing the request at once
  // would leave that handler nothing to revert.
  set: (open: boolean) => {
    if (!open) setTimeout(() => { pending.value = null; }, 0);
  },
});
const notice = shallowRef<string | null>(null);
const reverting = shallowRef(false);

function askRevertFile(path: string, status: FileDiffItem["status"]): void {
  pending.value = {
    path,
    hunk: null,
    title: "Revert this file?",
    detail:
      status === "added"
        ? "Deletes the file: the session created it. Its contents are lost."
        : status === "deleted"
          ? "Puts the deleted file back as it was."
          : "Puts the file back as it was before the agent changed it. The agent's edits to it are lost.",
  };
}
function askRevertHunk(path: string, hunk: ReviewHunk): void {
  pending.value = {
    path,
    hunk,
    title: "Revert this change?",
    detail: "Puts these lines back as they were. The agent's other edits to the file stay.",
  };
}

async function confirmRevert(): Promise<void> {
  const request = pending.value;
  pending.value = null;
  if (!request || reverting.value) return;
  const view = views.value[request.path];
  if (view?.entry?.status !== "ready") return;
  const item = view.entry.item;
  reverting.value = true;
  notice.value = null;
  try {
    if (item.status === "deleted" && !request.hunk) {
      // Gone from disk: put it back as the session found it. Refused if something new sits at that path.
      const result = await restoreSessionFile(machine, props.sessionId, request.path, item.before ?? "", null);
      if (!result.saved) notice.value = `${request.path} exists again, so nothing was reverted.`;
    } else if (item.status === "added" && !request.hunk) {
      // The session created it: reverting removes it, if it is still what the diff showed.
      const current = await readSessionFile(machine, props.sessionId, request.path);
      if ((current.content ?? "") !== (item.after ?? "")) {
        reload(request.path, versionOf(view.item));
        void sharedDiffs?.fetchDiffs();
        notice.value = `${request.path} changed while you were reviewing it. Its diff is refreshed; revert again if you still want to.`;
        return;
      }
      const result = await restoreSessionFile(machine, props.sessionId, request.path, null, current.hash ?? "");
      if (!result.saved) notice.value = `${request.path} changed on disk, so nothing was removed.`;
    } else {
      const current = await readSessionFile(machine, props.sessionId, request.path);
      // The file moved on since the diff was drawn: reverting from a stale picture would lose the newer edit.
      if ((current.content ?? "") !== (item.after ?? "")) {
        reload(request.path, versionOf(view.item));
        void sharedDiffs?.fetchDiffs();
        notice.value = `${request.path} changed while you were reviewing it. Its diff is refreshed; revert again if you still want to.`;
        return;
      }
      const text = request.hunk
        ? textWithRangeReverted(view.diffLines, request.hunk, view.trailingNewline, {
            before: item.before ?? "",
            after: item.after ?? "",
          })
        : (item.before ?? "");
      const result = await writeSessionFile(machine, props.sessionId, request.path, text, current.hash ?? "");
      if (!result.saved) notice.value = `${request.path} changed on disk, so nothing was reverted.`;
    }
    // Refresh the list first: a file that is no longer in the diff (reverted clean) has no contents to ask for.
    await sharedDiffs?.fetchDiffs();
    const remaining = allFiles.value.find((file) => file.file === request.path);
    if (remaining) reload(request.path, versionOf(remaining));
  } catch (error) {
    notice.value = `Could not revert ${request.path}: ${error instanceof Error ? error.message : String(error)}`;
  } finally {
    reverting.value = false;
  }
}

/** Why a file can't be reverted as a whole, or null when it can. */
function revertFileBlocker(file: FileDiffItem): string | null {
  if (file.isBinary || file.binary) return "A binary file can't be reverted from here";
  if (file.isTruncated || file.truncated) return "A file this large can't be reverted from here";
  return null;
}
function canRevertFile(file: FileDiffItem): boolean {
  return revertFileBlocker(file) === null;
}

async function copyPath(path: string): Promise<void> {
  try {
    await navigator.clipboard?.writeText(path);
  } catch {
    // clipboard unavailable
  }
}

// ─── Find in changes ─────────────────────────────────────────────────────────

const findBar = ref<InstanceType<typeof ChangesFindBar> | null>(null);
const findFiles = computed<FindFile[]>(() => {
  const out: FindFile[] = [];
  for (const file of files.value) {
    const view = views.value[file.file];
    if (view?.entry?.status === "ready" && !view.placeholder) out.push({ path: file.file, lines: findLines(view.items) });
  }
  return out;
});

/** Draws a match's line: opens its file, its unchanged run, the rest of a long diff, or its diff behind Rendered. */
function revealHit(hit: ChangesHit): void {
  if (!isOpen(hit.path)) toggleFile(hit.path);
  if (rendered.value.has(hit.path)) toggleRendered(hit.path, false);
  if (hit.inFold && !openFolds.value.has(foldKey(hit.path, hit.itemId))) openFold(hit.path, hit.itemId);
  const drawn = visibleItems(hit.path).some(
    (item) => item.id === hit.itemId && (item.kind === "fold" || item.lines.some((line) => line.index === hit.line)),
  );
  if (!drawn) showAll(hit.path);
}

const find = useChangesFind({
  files: findFiles,
  root,
  body,
  reveal: revealHit,
  focusField: () => {
    findBar.value?.field?.focus();
    findBar.value?.field?.select();
  },
  redraws: () => [layout.value, collapsed.value, openFolds.value, drawnAll.value, rendered.value, views.value],
});

// The machine says which changed files hold the text (one git grep of each side of the change set), and only those
// load: loading every file to search it meant hundreds of requests on a big change. A machine that can't say (an older
// Fleet, git failing) gets the old way, every file loading while the bar is open. git's -i folds ASCII letters only, so
// a query with others is searched the old way too.
// undefined: nothing asked (bar shut, nothing typed, or the answer is on its way); null: the machine can't say.
const serverMatches = shallowRef<ReadonlyMap<string, number> | null | undefined>(undefined);
const searchPending = shallowRef(false);
let searchRun = 0;
let searchTimer: ReturnType<typeof setTimeout> | undefined;
let searchAbort: AbortController | undefined;
watch(
  [find.isOpen, () => find.query.value.trim(), files],
  ([open, query]) => {
    clearTimeout(searchTimer);
    searchAbort?.abort();
    const run = ++searchRun;
    if (!open || query === "") {
      serverMatches.value = undefined;
      searchPending.value = false;
      return;
    }
    if (/[^\u0000-\u007f]/.test(query)) {
      serverMatches.value = null;
      searchPending.value = false;
      return;
    }
    searchPending.value = true;
    searchTimer = setTimeout(async () => {
      searchAbort = new AbortController();
      const found = await searchSessionChanges(machine, props.sessionId, query, searchAbort.signal);
      if (run !== searchRun) return;
      serverMatches.value = found;
      searchPending.value = false;
    }, 120);
  },
  { immediate: true },
);
onBeforeUnmount(() => {
  clearTimeout(searchTimer);
  searchAbort?.abort();
});

/** Whether find needs this file loaded: it holds the text, or the machine can't say. Lockfiles and very large diffs stay shut. */
function findWants(file: FileDiffItem): boolean {
  const matches = serverMatches.value;
  return !startsCollapsed(file) && matches !== undefined && (matches === null || matches.has(file.file));
}
watch(
  [find.isOpen, serverMatches, files],
  ([open]) => {
    if (!open) return;
    for (const file of files.value) {
      if (findWants(file)) load(file.file, versionOf(file));
    }
  },
  { immediate: true },
);
const findSearching = computed(
  () => searchPending.value || files.value.some((file) => findWants(file) && entries.value[file.file]?.status === "loading"),
);
// Files with matches that aren't searched here: lockfiles and very large diffs, shut. Without the machine's answer,
// every such file counts.
const findUnsearched = computed(
  () => files.value.filter((file) =>
    startsCollapsed(file)
    && !isOpen(file.file)
    && entries.value[file.file]?.status !== "ready"
    && (serverMatches.value == null || serverMatches.value.has(file.file)),
  ).length,
);

// ─── Selected lines: Copy link, Quote, Add to message ────────────────────────

const annotate = useCanvasAnnotate();
function onAnnotate(path: string, anchor: AnnotationAnchor, position: { x: number; y: number }): void {
  annotate(anchor, position, path);
}

/**
 * The side a drag in Split started on: the other column stays out of the selection. Set on the element at once,
 * not through a render, on pointerdown: the browser starts the selection on the mousedown that follows.
 */
let pickSide: "old" | "new" | null = null;
function onBodyPointerDown(event: PointerEvent): void {
  const side = (event.target as Element | null)?.closest<HTMLElement>(".rline[data-side]")?.dataset.side;
  const next = side === "old" || side === "new" ? side : null;
  // A selection down one column runs through the other column's rows in between, so a press there lands "inside"
  // it and would drag it instead of starting a new one. Let it go first.
  if (next && pickSide && next !== pickSide) document.getSelection()?.removeAllRanges();
  pickSide = next;
  if (pickSide) root.value?.setAttribute("data-pick", pickSide);
  else root.value?.removeAttribute("data-pick");
}

const selected = shallowRef<(ReviewSelection & { path: string }) | null>(null);

function elementOf(node: Node | null): Element | null {
  return node instanceof Element ? node : (node?.parentElement ?? null);
}
function lineNumber(value: string | undefined): number | null {
  return value ? Number(value) : null;
}
/** Whether the selection takes in none of the row's text: it starts at the end of the row, or ends at its start. */
function touchesOnly(row: HTMLElement, range: Range, edge: "first" | "last"): boolean {
  const text = row.querySelector(".rline__tx");
  if (!text?.textContent) return false;
  const part = document.createRange();
  if (edge === "first") {
    part.setStart(range.startContainer, range.startOffset);
    part.setEnd(text, text.childNodes.length);
  } else {
    part.setStart(text, 0);
    part.setEnd(range.endContainer, range.endOffset);
  }
  return part.toString() === "";
}

/** The lines the page's text selection spans in one file of the review, or null when it isn't in the review. */
function readSelection(): void {
  const selection = document.getSelection();
  let next: (ReviewSelection & { path: string }) | null = null;
  const anchor = elementOf(selection?.anchorNode ?? null);
  const section = anchor?.closest<HTMLElement>("[data-review-file]");
  if (selection && selection.rangeCount > 0 && !selection.isCollapsed && section && body.value?.contains(section)) {
    const range = selection.getRangeAt(0);
    const side = layout.value === "split"
      ? (pickSide ?? anchor?.closest<HTMLElement>(".rline[data-side]")?.dataset.side ?? null)
      : null;
    const rows = [...section.querySelectorAll<HTMLElement>(".rline[data-old], .rline[data-new]")].filter(
      (row) => range.intersectsNode(row) && (!side || row.dataset.side === side),
    );
    if (rows.length > 1 && touchesOnly(rows[0]!, range, "first")) rows.shift();
    if (rows.length > 1 && touchesOnly(rows[rows.length - 1]!, range, "last")) rows.pop();
    const lines = selectionLines(
      rows.map((row): SelectedRow => ({ old: lineNumber(row.dataset.old), new: lineNumber(row.dataset.new) })),
    );
    if (lines) next = { ...lines, path: section.dataset.reviewFile! };
  }
  const current = selected.value;
  if (current === next || (current && next && current.path === next.path && sameSelection(current, next))) return;
  selected.value = next;
}
useEventListener(document, "selectionchange", readSelection);

const selectedLabel = computed(() => {
  const current = selected.value;
  if (!current) return "";
  return `${current.side === "old" ? "Old " : ""}${lineRangeLabel(current.range)}`;
});
const selectedCount = computed(() => (selected.value ? lineCountLabel(selected.value.range) : ""));
/** Old lines have no place in the file as it is, so they have no link; they're quoted instead. */
const selectedLink = computed(() =>
  selected.value?.side === "new" ? lineReference(selected.value.path, selected.value.range) : null,
);

function selectedQuote(): string | null {
  const current = selected.value;
  const entry = current ? views.value[current.path]?.entry : undefined;
  if (!current || entry?.status !== "ready") return null;
  const text = sliceLines((current.side === "new" ? entry.item.after : entry.item.before) ?? "", current.range);
  const reference = lineReference(current.path, current.range);
  return lineQuote(current.side === "new" ? reference : `${reference} (before)`, current.path, text);
}

const toastMessage = shallowRef<string | null>(null);
let toastTimer: ReturnType<typeof setTimeout> | undefined;
function toast(message: string): void {
  toastMessage.value = message;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => {
    toastMessage.value = null;
  }, 2200);
}
onBeforeUnmount(() => clearTimeout(toastTimer));

async function copyText(text: string, done: string): Promise<void> {
  try {
    await navigator.clipboard.writeText(text);
    toast(done);
  } catch {
    toast("Couldn't copy to the clipboard");
  }
}
function copySelectedLink(): void {
  if (selectedLink.value) void copyText(selectedLink.value, `Copied ${selectedLink.value}`);
}
function copySelectedQuote(): void {
  const quote = selectedQuote();
  if (quote && selected.value) void copyText(quote, `Copied ${lineRangeLabel(selected.value.range)} as a quote`);
}
function clearSelected(): void {
  document.getSelection()?.removeAllRanges();
  selected.value = null;
}
/** Lines of the file as it is go in as an @ reference, like a file tab's; old lines go in quoted. */
function addSelectedToMessage(): void {
  const current = selected.value;
  if (!current) return;
  if (current.side === "new") appendDraftReference(props.sessionId, `@${lineReference(current.path, current.range)}`);
  else {
    const quote = selectedQuote();
    if (!quote) return;
    appendDraftText(props.sessionId, quote);
  }
  clearSelected();
  runSessionCommand("focus-prompt", props.sessionId);
}

// ─── Scroll to a file asked for from the changed-files card ─────────────────

const focusRequest = computed(() => canvases.sessionCanvases(props.sessionId).reviewFocus);
let handledSeq = 0;
watch(
  [focusRequest, files],
  async ([request]) => {
    if (!request || request.seq === handledSeq) return;
    if (!allFiles.value.some((file) => file.file === request.path)) return;
    handledSeq = request.seq;
    if (!isOpen(request.path)) toggleFile(request.path);
    await nextTick();
    const sections = body.value?.querySelectorAll<HTMLElement>("[data-review-file]") ?? [];
    for (const section of sections) {
      if (section.dataset.reviewFile === request.path) {
        section.scrollIntoView({ block: "start", behavior: "smooth" });
        break;
      }
    }
  },
  { immediate: true, flush: "post" },
);

function dirOf(path: string): string {
  const slash = path.lastIndexOf("/");
  return slash >= 0 ? path.slice(0, slash + 1) : "";
}
function nameOf(path: string): string {
  return path.slice(path.lastIndexOf("/") + 1);
}

/**
 * Line numbers stick to the left only in a hunk you've scrolled sideways. A sticky number on every line made each
 * repaint of the page (every keystroke in the composer beside it) lay out and hit-test thousands of sticky boxes;
 * unscrolled, a static number sits exactly where a sticky one would, so nothing moves.
 */
function onCodeScroll(event: Event): void {
  const scroller = event.target;
  if (!(scroller instanceof HTMLElement) || !scroller.classList.contains("rscroll")) return;
  scroller.classList.toggle("rscroll--scrolled", scroller.scrollLeft > 0);
}
</script>

<template>
  <div
    ref="root"
    class="review"
    :class="{ 'review--wrap': wrap }"
    tabindex="-1"
    data-testid="changes-review"
    @keydown="find.onKeydown"
    @scroll.capture.passive="onCodeScroll"
  >
    <header class="review__head">
      <h2 class="review__title">
        Changes
      </h2>
      <span
        class="review__scope"
        data-testid="review-scope"
        :title="baseDetail"
      >Whole session</span>
      <span
        class="review__totals"
        data-testid="review-totals"
      >
        <span class="review__add">+{{ totals.additions }}</span>
        <span class="review__del">−{{ totals.deletions }}</span>
      </span>
      <span class="review__spacer" />
      <div
        class="review__seg"
        role="group"
        aria-label="Layout"
      >
        <button
          type="button"
          :aria-pressed="layout === 'unified'"
          data-testid="review-unified"
          @click="setLayout('unified')"
        >
          Unified
        </button>
        <button
          type="button"
          :aria-pressed="layout === 'split'"
          data-testid="review-split"
          @click="setLayout('split')"
        >
          Split
        </button>
      </div>
      <button
        type="button"
        class="review__icon"
        :aria-pressed="find.isOpen.value"
        title="Find in changes (Ctrl+F)"
        aria-label="Find in changes"
        data-testid="review-find"
        @click="find.isOpen.value ? find.close() : find.open()"
      >
        <Search
          :size="15"
          aria-hidden="true"
        />
      </button>
      <button
        type="button"
        class="review__icon"
        :aria-pressed="wrap"
        title="Wrap long lines"
        aria-label="Wrap long lines"
        data-testid="review-wrap"
        @click="toggleWrap"
      >
        <WrapText
          :size="15"
          aria-hidden="true"
        />
      </button>
      <button
        type="button"
        class="review__icon"
        :title="allCollapsed ? 'Expand all files' : 'Collapse all files'"
        :aria-label="allCollapsed ? 'Expand all files' : 'Collapse all files'"
        data-testid="review-collapse"
        @click="toggleAll"
      >
        <ChevronsUpDown
          v-if="allCollapsed"
          :size="15"
          aria-hidden="true"
        />
        <ChevronsDownUp
          v-else
          :size="15"
          aria-hidden="true"
        />
      </button>
    </header>

    <ChangesFindBar
      v-if="find.isOpen.value"
      ref="findBar"
      :query="find.query.value"
      :total="find.hits.value.length"
      :current="find.current.value"
      :searching="findSearching"
      :unsearched="findUnsearched"
      @update:query="find.query.value = $event"
      @step="find.step"
      @close="find.close"
    />

    <div
      ref="body"
      class="review__body"
      @pointerdown="onBodyPointerDown"
    >
      <p
        v-if="notice"
        class="review__notice"
        role="alert"
        data-testid="review-notice"
      >
        {{ notice }}
        <button
          type="button"
          @click="notice = null"
        >
          Dismiss
        </button>
      </p>

      <section
        v-for="file in files"
        :key="file.file"
        class="rfile"
        :class="{ 'rfile--collapsed': !isOpen(file.file) }"
        :data-review-file="file.file"
        data-testid="review-file"
      >
        <header
          class="rfile__head"
          role="button"
          tabindex="0"
          :aria-expanded="isOpen(file.file)"
          @click="toggleFile(file.file)"
          @keydown.enter.self.prevent="toggleFile(file.file)"
          @keydown.space.self.prevent="toggleFile(file.file)"
        >
          <ChevronDown
            :size="14"
            class="rfile__chev"
            aria-hidden="true"
          />
          <FileText
            :size="15"
            class="rfile__icon"
            aria-hidden="true"
          />
          <span
            class="rfile__path"
            :title="file.file"
          >
            <span class="rfile__dir">{{ dirOf(file.file) }}</span>{{ nameOf(file.file) }}
          </span>
          <span class="rfile__counts">
            <span class="review__add">+{{ file.additions }}</span>
            <span class="review__del">−{{ file.deletions }}</span>
          </span>
          <span
            class="rfile__acts"
            @click.stop
          >
            <div
              v-if="hasRenderedView(file.file)"
              class="review__seg review__seg--small"
              role="group"
              aria-label="View"
            >
              <button
                type="button"
                :aria-pressed="!rendered.has(file.file)"
                @click="toggleRendered(file.file, false)"
              >
                Diff
              </button>
              <button
                type="button"
                :aria-pressed="rendered.has(file.file)"
                data-testid="review-rendered"
                @click="toggleRendered(file.file, true)"
              >
                Rendered
              </button>
            </div>
            <button
              type="button"
              class="rfile__act"
              :disabled="!canRevertFile(file) || reverting"
              :title="revertFileBlocker(file) ?? (file.status === 'added' ? 'Revert file: delete this new file' : 'Revert file')"
              data-testid="review-revert-file"
              @click="askRevertFile(file.file, file.status)"
            >
              <Undo2
                :size="14"
                aria-hidden="true"
              />
              Revert file
            </button>
            <button
              type="button"
              class="rfile__act"
              title="Open file"
              aria-label="Open file"
              data-testid="review-open-file"
              @click="openInEditor(file)"
            >
              <ExternalLink
                :size="14"
                aria-hidden="true"
              />
            </button>
            <button
              type="button"
              class="rfile__act"
              title="Copy path"
              aria-label="Copy path"
              @click="copyPath(file.file)"
            >
              <Copy
                :size="14"
                aria-hidden="true"
              />
            </button>
          </span>
        </header>

        <div
          v-if="isOpen(file.file)"
          class="rfile__body"
        >
          <p
            v-if="!views[file.file]?.entry || views[file.file]?.entry?.status === 'loading'"
            class="rfile__note"
          >
            Loading…
          </p>
          <p
            v-else-if="views[file.file]?.entry?.status === 'error'"
            class="rfile__note"
          >
            Couldn't load this diff.
            <button
              type="button"
              class="rfile__retry"
              data-testid="review-file-retry"
              @click="reload(file.file, versionOf(file))"
            >
              Try again
            </button>
          </p>
          <p
            v-else-if="views[file.file]?.placeholder"
            class="rfile__note"
          >
            {{ views[file.file]?.placeholder }}
          </p>
          <component
            :is="rendererFor(file.file)"
            v-else-if="rendered.has(file.file) && views[file.file]?.entry?.status === 'ready' && rendererFor(file.file)"
            :content="(views[file.file]?.entry as { item: FileDiffItem }).item.after"
            :annotatable="!/\.html?$/i.test(file.file)"
            class="rfile__rendered"
            @annotate="(anchor: AnnotationAnchor, position: { x: number; y: number }) => onAnnotate(file.file, anchor, position)"
          />
          <p
            v-else-if="(views[file.file]?.items.length ?? 0) === 0"
            class="rfile__note"
          >
            No line changes.
          </p>
          <template v-else>
            <template
              v-for="item in visibleItems(file.file)"
              :key="item.id"
            >
              <!-- A run of unchanged lines: folded until opened -->
              <template v-if="item.kind === 'fold'">
                <button
                  v-if="foldLines(file.file, item).length === 0"
                  type="button"
                  class="rfold"
                  data-testid="review-fold"
                  @click="openFold(file.file, item.id)"
                >
                  <Ellipsis
                    :size="14"
                    aria-hidden="true"
                  />
                  {{ item.hidden }} unchanged {{ item.hidden === 1 ? "line" : "lines" }}
                </button>
                <div
                  v-else-if="layout === 'unified'"
                  class="rscroll"
                >
                  <div class="rinner">
                    <div
                      v-for="line in item.lines"
                      :key="line.index"
                      class="rline rline--context"
                      :data-old="line.oldLineNumber"
                      :data-new="line.newLineNumber"
                    >
                      <span class="rline__no">{{ line.oldLineNumber }}</span>
                      <span class="rline__no rline__no--new">{{ line.newLineNumber }}</span>
                      <span class="rline__mk" />
                      <span
                        class="rline__tx"
                        :data-find-line="line.index"
                      >{{ line.content }}</span>
                    </div>
                  </div>
                </div>
                <div
                  v-else
                  class="rsplit"
                >
                  <div
                    v-for="row in splitFoldRows(item.lines)"
                    :key="row.left!.index"
                    class="rsplit__row"
                  >
                    <div
                      class="rline rline--context"
                      data-side="old"
                      :data-old="row.left!.oldLineNumber"
                    >
                      <span class="rline__no">{{ row.left!.oldLineNumber }}</span>
                      <span class="rline__mk" />
                      <span
                        class="rline__tx"
                        :data-find-line="row.left!.index"
                      >{{ row.left!.content }}</span>
                    </div>
                    <div
                      class="rline rline--context"
                      data-side="new"
                      :data-new="row.right!.newLineNumber"
                    >
                      <span class="rline__no">{{ row.right!.newLineNumber }}</span>
                      <span class="rline__mk" />
                      <span
                        class="rline__tx"
                        :data-find-line="row.right!.index"
                      >{{ row.right!.content }}</span>
                    </div>
                  </div>
                </div>
              </template>

              <template v-else>
                <div
                  class="rhunk"
                  data-testid="review-hunk"
                >
                  <span class="rhunk__at">@@</span>
                  <span class="rhunk__name">{{ item.name || `line ${item.startLine}` }}</span>
                  <button
                    type="button"
                    class="rhunk__revert"
                    :disabled="reverting || file.status !== 'modified'"
                    :title="file.status === 'modified' ? 'Revert hunk' : 'Revert the whole file instead'"
                    data-testid="review-revert-hunk"
                    @click="askRevertHunk(file.file, item)"
                  >
                    <Undo2
                      :size="12"
                      aria-hidden="true"
                    />
                    Revert hunk
                  </button>
                </div>

                <div
                  v-if="layout === 'unified'"
                  class="rscroll"
                >
                  <div class="rinner">
                    <div
                      v-for="line in item.lines"
                      :key="line.index"
                      class="rline"
                      :class="`rline--${line.type}`"
                      data-testid="review-line"
                      :data-diff-type="line.type"
                      :data-old="line.oldLineNumber"
                      :data-new="line.newLineNumber"
                    >
                      <span class="rline__no">{{ line.oldLineNumber }}</span>
                      <span class="rline__no rline__no--new">{{ line.newLineNumber }}</span>
                      <span class="rline__mk">{{ line.type === "add" ? "+" : line.type === "remove" ? "−" : "" }}</span>
                      <span
                        class="rline__tx"
                        :data-find-line="line.index"
                      ><template
                        v-for="(part, n) in segments(line)"
                        :key="n"
                      ><mark
                        v-if="part.marked"
                        :class="`mark--${line.type}`"
                      >{{ part.text }}</mark><template v-else>{{ part.text }}</template></template></span>
                    </div>
                  </div>
                </div>

                <div
                  v-else
                  class="rsplit"
                >
                  <div
                    v-for="(row, n) in hunkRows(item)"
                    :key="n"
                    class="rsplit__row"
                  >
                    <div
                      class="rline"
                      :class="row.left ? `rline--${row.left.type}` : 'rline--pad'"
                      data-side="old"
                      :data-old="row.left?.oldLineNumber"
                    >
                      <span class="rline__no">{{ row.left?.oldLineNumber }}</span>
                      <span class="rline__mk">{{ row.left?.type === "remove" ? "−" : "" }}</span>
                      <span
                        class="rline__tx"
                        :data-find-line="row.left?.index"
                      ><template v-if="row.left"><template
                        v-for="(part, k) in segments(row.left)"
                        :key="k"
                      ><mark
                        v-if="part.marked"
                        class="mark--remove"
                      >{{ part.text }}</mark><template v-else>{{ part.text }}</template></template></template></span>
                    </div>
                    <div
                      class="rline"
                      :class="row.right ? `rline--${row.right.type}` : 'rline--pad'"
                      data-side="new"
                      :data-new="row.right?.newLineNumber"
                    >
                      <span class="rline__no">{{ row.right?.newLineNumber }}</span>
                      <span class="rline__mk">{{ row.right?.type === "add" ? "+" : "" }}</span>
                      <span
                        class="rline__tx"
                        :data-find-line="row.right?.index"
                      ><template v-if="row.right"><template
                        v-for="(part, k) in segments(row.right)"
                        :key="k"
                      ><mark
                        v-if="part.marked"
                        class="mark--add"
                      >{{ part.text }}</mark><template v-else>{{ part.text }}</template></template></template></span>
                    </div>
                  </div>
                </div>
              </template>
            </template>
            <button
              v-if="hiddenChanges(file.file) > 0"
              type="button"
              class="rfold"
              data-testid="review-show-all"
              @click="showAll(file.file)"
            >
              Show {{ hiddenChanges(file.file) }} more lines
            </button>
          </template>
        </div>
      </section>
    </div>

    <LinesBar
      v-if="selected"
      :file="nameOf(selected.path)"
      :label="selectedLabel"
      :count="selectedCount"
      :link="selectedLink"
      @copy-link="copySelectedLink"
      @quote="copySelectedQuote"
      @add="addSelectedToMessage"
      @clear="clearSelected"
    />
    <div
      class="review__toast"
      :class="{ 'is-shown': toastMessage, 'is-lifted': selected }"
      role="status"
      aria-live="polite"
    >
      {{ toastMessage }}
    </div>

    <AlertDialog
      :open="confirmOpen"
      @update:open="confirmOpen = $event"
    >
      <AlertDialogContent>
        <AlertDialogHeader>
          <AlertDialogTitle>{{ pending?.title }}</AlertDialogTitle>
          <AlertDialogDescription>
            <span class="block font-mono text-foreground">{{ pending?.path }}</span>
            <span class="block">{{ pending?.detail }}</span>
          </AlertDialogDescription>
        </AlertDialogHeader>
        <AlertDialogFooter>
          <AlertDialogCancel>Cancel</AlertDialogCancel>
          <AlertDialogAction
            data-testid="review-revert-confirm"
            @click="confirmRevert"
          >
            Revert
          </AlertDialogAction>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  </div>
</template>

<style scoped>
.review {
  --add-bg: color-mix(in srgb, var(--diff-add) 10%, transparent);
  --del-bg: color-mix(in srgb, var(--diff-del) 9%, transparent);
  --add-word: color-mix(in srgb, var(--diff-add) 26%, transparent);
  --del-word: color-mix(in srgb, var(--diff-del) 24%, transparent);
  position: relative;
  flex: 1;
  min-height: 0;
  min-width: 0;
  display: flex;
  flex-direction: column;
}

/* Focusable so a click in the diff gives Changes its Ctrl/⌘F; no ring for that. */
.review:focus {
  outline: none;
}

.review__head {
  flex: none;
  display: flex;
  align-items: center;
  gap: 10px;
  flex-wrap: wrap;
  min-height: 46px;
  padding: 6px 10px 6px 14px;
  border-bottom: 1px solid var(--border);
  font-size: 12.5px;
}

.review__title {
  margin: 0;
  font-size: 14px;
  font-weight: 600;
}

.review__spacer {
  flex: 1;
}

.review__scope {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  height: 28px;
  padding: 0 8px 0 10px;
  white-space: nowrap;
  color: var(--text-muted, var(--text));
  font: inherit;
}

.review__totals,
.rfile__counts {
  display: inline-flex;
  gap: 6px;
  font-family: var(--font-mono-stack);
  font-size: 12px;
  font-variant-numeric: tabular-nums;
}

.review__add {
  color: var(--diff-add);
  font-weight: 500;
}

.review__del {
  color: var(--diff-del);
  font-weight: 500;
}

.review__seg {
  display: inline-flex;
  gap: 2px;
  padding: 2px;
  border-radius: 8px;
  background: color-mix(in srgb, var(--text) 6%, transparent);
}

.review__seg button {
  padding: 3px 10px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--muted);
  font: inherit;
  font-weight: 500;
  cursor: pointer;
}

.review__seg button[aria-pressed="true"] {
  background: var(--panel-bg);
  color: var(--text);
  box-shadow: 0 0 0 1px var(--border);
}

.review__seg--small button {
  padding: 1px 8px;
  font-size: 12px;
}

.review__icon {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 28px;
  height: 28px;
  border: 0;
  border-radius: 7px;
  background: transparent;
  color: var(--muted);
  cursor: pointer;
}

.review__icon:hover {
  background: color-mix(in srgb, var(--text) 7%, transparent);
  color: var(--text);
}

.review__icon[aria-pressed="true"] {
  background: var(--accent-dim);
  color: var(--accent);
}

.review__body {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  overflow-x: hidden;
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 12px 12px 16px;
}

.review__notice {
  flex: none;
  display: flex;
  gap: 10px;
  align-items: baseline;
  margin: 0;
  padding: 8px 10px;
  border: 1px solid color-mix(in srgb, var(--error) 40%, var(--border));
  border-radius: 8px;
  font-size: 12.5px;
}

.review__notice button {
  margin-left: auto;
  border: 0;
  background: transparent;
  color: var(--muted);
  cursor: pointer;
  font: inherit;
}

.rfile {
  flex: none;
  border: 1px solid var(--border);
  border-radius: 8px;
  background: var(--panel-bg);
  /* Rounded corners clip the code; the sticky header still sticks inside the scrolling body. */
  overflow: clip;
}

.rfile__head {
  position: sticky;
  top: -12px;
  z-index: 2;
  display: flex;
  align-items: center;
  gap: 8px;
  height: 36px;
  padding: 0 10px 0 8px;
  border-bottom: 1px solid var(--border);
  background: color-mix(in srgb, var(--text) 4%, var(--panel-bg));
  cursor: pointer;
}

.rfile--collapsed .rfile__head {
  border-bottom: 0;
}

.rfile__head:hover {
  background: color-mix(in srgb, var(--text) 7%, var(--panel-bg));
}

.rfile__head:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -2px;
}

.rfile__chev {
  flex: none;
  color: var(--muted);
  transition: transform 120ms;
}

.rfile--collapsed .rfile__chev {
  transform: rotate(-90deg);
}

.rfile__icon {
  flex: none;
  color: var(--muted);
}

.rfile__path {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-family: var(--font-mono-stack);
  font-size: 12.5px;
  font-weight: 500;
}

.rfile__dir {
  color: var(--muted);
  font-weight: 400;
}

.rfile__acts {
  margin-left: auto;
  display: none;
  align-items: center;
  gap: 2px;
}

.rfile__head:hover .rfile__acts,
.rfile__head:focus-within .rfile__acts {
  display: flex;
}

.rfile__counts {
  flex: none;
}

/* No hover on touch screens: the file's actions stay in view. */
@media (hover: none) {
  .rfile__acts {
    display: flex;
  }

  .rfile__act:not([title="Copy path"]) {
    display: none;
  }
}

.rfile__act {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  height: 24px;
  padding: 0 6px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--muted);
  font: inherit;
  font-size: 12px;
  cursor: pointer;
}

.rfile__act:hover:not(:disabled) {
  background: color-mix(in srgb, var(--text) 9%, transparent);
  color: var(--text);
}

.rfile__act:disabled {
  opacity: 0.45;
  cursor: default;
}

.rfile__note {
  margin: 0;
  padding: 12px 14px;
  color: var(--muted);
  font-size: 12.5px;
}

.rfile__retry {
  margin-left: 8px;
  padding: 2px 10px;
  border: 1px solid var(--border);
  background: transparent;
  color: var(--text);
  font: inherit;
  cursor: pointer;
}

.rfile__rendered {
  padding: 8px 14px;
}

.rhunk {
  display: flex;
  align-items: center;
  gap: 8px;
  height: 28px;
  padding: 0 6px 0 12px;
  background: var(--accent-dim);
  color: var(--muted);
  font-family: var(--font-mono-stack);
  font-size: 11.5px;
  white-space: nowrap;
}

.rhunk__at {
  color: var(--accent);
  font-weight: 500;
}

.rhunk__name {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
}

.rhunk__revert {
  margin-left: auto;
  display: inline-flex;
  align-items: center;
  gap: 5px;
  flex: none;
  height: 22px;
  padding: 0 8px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--muted);
  font: 500 11.5px var(--font-sans, system-ui);
  cursor: pointer;
}

.rhunk__revert:hover:not(:disabled) {
  background: color-mix(in srgb, var(--text) 9%, transparent);
  color: var(--text);
}

.rhunk__revert:disabled {
  opacity: 0.45;
  cursor: default;
}

.rfold {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  height: 26px;
  padding: 0 12px;
  border: 0;
  border-block: 1px solid var(--border);
  background: color-mix(in srgb, var(--text) 3%, var(--panel-bg));
  color: var(--muted);
  font: 12px var(--font-sans, system-ui);
  text-align: left;
  cursor: pointer;
}

.rfold:hover {
  background: color-mix(in srgb, var(--text) 7%, var(--panel-bg));
  color: var(--text);
}

.rscroll {
  overflow-x: auto;
  scrollbar-width: none;
  mask-image: linear-gradient(to right, #000 calc(100% - 28px), transparent);
}

.rinner {
  min-width: max-content;
}

.review--wrap .rscroll {
  mask-image: none;
}

.review--wrap .rinner {
  min-width: 0;
}

.rline {
  display: flex;
  min-width: 100%;
  font-family: var(--font-mono-stack);
  font-size: 12px;
  line-height: 20px;
  white-space: pre;
  font-variant-ligatures: none;
}

.review--wrap .rline {
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

/* left and z-index apply once the hunk is scrolled sideways and the numbers stick (.rscroll--scrolled). */
.rline__no {
  left: 0;
  z-index: 1;
  flex: none;
  width: 36px;
  padding-right: 8px;
  background: var(--panel-bg);
  color: color-mix(in srgb, var(--muted) 65%, transparent);
  font-size: 11.5px;
  text-align: right;
  user-select: none;
}

.rline__no--new {
  left: 36px;
  border-right: 1px solid var(--border);
}

/* Sticky only in a hunk scrolled sideways (onCodeScroll): a sticky number on every line slowed every repaint. */
.rscroll--scrolled .rline__no {
  position: sticky;
}

.rline__mk {
  flex: none;
  width: 20px;
  color: var(--muted);
  text-align: center;
  user-select: none;
}

.rline__tx {
  flex: 1;
  min-width: 0;
  padding-right: 16px;
}
/* A drag in Split selects down its own column, not across both. */
.review[data-pick="old"] .rline[data-side="new"] .rline__tx,
.review[data-pick="new"] .rline[data-side="old"] .rline__tx {
  user-select: none;
}

.review__toast {
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
.review__toast.is-lifted {
  bottom: 58px;
}
.review__toast.is-shown {
  opacity: 1;
  transform: translate(-50%, 0);
}
@media (prefers-reduced-motion: reduce) {
  .review__toast {
    transition: none;
  }
}

.rline--add {
  background: var(--add-bg);
}

.rline--add .rline__no {
  background: linear-gradient(var(--add-bg), var(--add-bg)), var(--panel-bg);
}

.rline--add .rline__mk {
  color: var(--diff-add);
}

.rline--remove {
  background: var(--del-bg);
}

.rline--remove .rline__no {
  background: linear-gradient(var(--del-bg), var(--del-bg)), var(--panel-bg);
}

.rline--remove .rline__mk {
  color: var(--diff-del);
}

.rline--pad {
  background: repeating-linear-gradient(135deg, transparent 0 4px, color-mix(in srgb, var(--text) 6%, transparent) 4px 5px);
}

mark {
  border-radius: 2px;
  color: inherit;
}

.mark--add {
  background: var(--add-word);
}

.mark--remove {
  background: var(--del-word);
}

.rsplit {
  /* One grid per hunk: both panes share the same columns, so a long line widens
     them together and a single horizontal scroll moves old and new as one. */
  display: grid;
  grid-template-columns: repeat(2, minmax(max-content, 1fr));
  overflow-x: auto;
  scrollbar-width: thin;
}

.review--wrap .rsplit {
  grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);
  overflow-x: hidden;
}

.rsplit__row {
  display: contents;
}

.rsplit__row > .rline {
  min-width: 0;
}

.rsplit__row > .rline + .rline {
  border-left: 1px solid var(--border);
}

.rsplit .rline__no {
  position: static;
  width: 34px;
}
</style>
