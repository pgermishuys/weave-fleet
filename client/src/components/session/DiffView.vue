<script setup lang="ts">
import { computed, shallowRef, watch } from "vue";
import type { DiffLine } from "@/lib/diff-parser";

const CONTEXT_EDGE_LINE_COUNT = 3;
const COLLAPSE_CONTEXT_AFTER_LINE_COUNT = 10;

type RenderedDiffRow = RenderedLineRow | RenderedExpandRow;

interface RenderedLineRow {
  kind: "line";
  key: string;
  line: DiffLine;
  marker: "+" | "-" | "";
  rowClass: string;
  originalIndex: number;
}

interface RenderedExpandRow {
  kind: "expand";
  key: string;
  id: string;
  hiddenLineCount: number;
}

const props = defineProps<{
  lines: DiffLine[];
}>();

const expandedContextRunIds = shallowRef<ReadonlySet<string>>(new Set());

const renderedRows = computed<RenderedDiffRow[]>(() => {
  const rows: RenderedDiffRow[] = [];
  let lineIndex = 0;

  while (lineIndex < props.lines.length) {
    const line = props.lines[lineIndex];

    if (line?.type !== "context") {
      if (line !== undefined) {
        rows.push(createLineRow(line, lineIndex));
      }

      lineIndex += 1;
      continue;
    }

    const contextStartIndex = lineIndex;

    while (props.lines[lineIndex]?.type === "context") {
      lineIndex += 1;
    }

    addContextRows(rows, contextStartIndex, lineIndex);
  }

  return rows;
});

watch(
  () => props.lines,
  () => {
    expandedContextRunIds.value = new Set();
  },
);

function addContextRows(rows: RenderedDiffRow[], startIndex: number, endIndex: number): void {
  const runLength = endIndex - startIndex;
  const runId = createContextRunId(startIndex, endIndex);

  if (runLength <= COLLAPSE_CONTEXT_AFTER_LINE_COUNT || expandedContextRunIds.value.has(runId)) {
    addLineRange(rows, startIndex, endIndex);
    return;
  }

  const leadingEndIndex = startIndex + CONTEXT_EDGE_LINE_COUNT;
  const trailingStartIndex = endIndex - CONTEXT_EDGE_LINE_COUNT;

  addLineRange(rows, startIndex, leadingEndIndex);
  rows.push({
    kind: "expand",
    key: `expand-${runId}`,
    id: runId,
    hiddenLineCount: trailingStartIndex - leadingEndIndex,
  });
  addLineRange(rows, trailingStartIndex, endIndex);
}

function addLineRange(rows: RenderedDiffRow[], startIndex: number, endIndex: number): void {
  for (let index = startIndex; index < endIndex; index += 1) {
    const line = props.lines[index];

    if (line !== undefined) {
      rows.push(createLineRow(line, index));
    }
  }
}

function createLineRow(line: DiffLine, originalIndex: number): RenderedLineRow {
  return {
    kind: "line",
    key: `line-${originalIndex}-${line.oldLineNumber ?? ""}-${line.newLineNumber ?? ""}`,
    line,
    marker: getDiffMarker(line.type),
    rowClass: `diff-line diff-line--${line.type}`,
    originalIndex,
  };
}

function createContextRunId(startIndex: number, endIndex: number): string {
  return `${startIndex}-${endIndex}`;
}

function expandContextRun(id: string): void {
  expandedContextRunIds.value = new Set([...expandedContextRunIds.value, id]);
}

function getDiffMarker(type: DiffLine["type"]): "+" | "-" | "" {
  if (type === "add") {
    return "+";
  }

  if (type === "remove") {
    return "-";
  }

  return "";
}
</script>

<template>
  <div
    class="diff-view"
    aria-label="Diff view"
    data-testid="tool-card-diff"
  >
    <template
      v-for="row in renderedRows"
      :key="row.key"
    >
      <div
        v-if="row.kind === 'line'"
        :class="row.rowClass"
        data-testid="tool-card-diff-row"
        :data-diff-type="row.line.type"
        :data-diff-index="row.originalIndex"
        :data-old-line-number="row.line.oldLineNumber"
        :data-new-line-number="row.line.newLineNumber"
      >
        <span
          class="diff-line__number diff-line__number--old"
          aria-hidden="true"
        >{{ row.line.oldLineNumber ?? "" }}</span>
        <span
          class="diff-line__number diff-line__number--new"
          aria-hidden="true"
        >{{ row.line.newLineNumber ?? "" }}</span>
        <span
          class="diff-line__marker"
          aria-hidden="true"
        >{{ row.marker }}</span>
        <span class="diff-line__content">{{ row.line.content }}</span>
      </div>

      <div
        v-else
        class="diff-expand"
      >
        <span class="diff-expand__gutter" />
        <button
          type="button"
          class="diff-expand__button"
          @click="expandContextRun(row.id)"
        >
          Show {{ row.hiddenLineCount }} unchanged {{ row.hiddenLineCount === 1 ? "line" : "lines" }}
        </button>
      </div>
    </template>
  </div>
</template>

<style scoped>
/* Theme-aware diff: tints are mixed into the panel colour so the sticky
   line-number columns stay opaque when scrolling sideways. */
.diff-view {
  --diff-add: color-mix(in srgb, var(--running) 13%, var(--panel-bg));
  --diff-remove: color-mix(in srgb, var(--error) 12%, var(--panel-bg));
  --diff-gutter: color-mix(in srgb, var(--text) 3%, var(--panel-bg));
  width: 100%;
  min-width: 0;
  overflow: auto;
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  background: var(--panel-bg);
}

.diff-line {
  display: grid;
  grid-template-columns: 42px 42px 22px minmax(0, 1fr);
  align-items: start;
  min-width: max-content;
  border-left: 2px solid transparent;
  font-family: var(--font-mono-stack);
  font-size: 12px;
  line-height: 1.6;
  color: color-mix(in srgb, var(--text) 88%, transparent);
  white-space: pre;
}

.diff-line__number {
  position: sticky;
  z-index: 1;
  display: inline-block;
  min-height: 100%;
  padding: 1px 8px;
  border-right: 1px solid var(--border);
  background: var(--diff-gutter);
  color: color-mix(in srgb, var(--muted) 80%, transparent);
  text-align: right;
  font-variant-numeric: tabular-nums;
  user-select: none;
}

.diff-line__number--old {
  left: 0;
}

.diff-line__number--new {
  left: 42px;
}

.diff-line__marker {
  padding: 1px 6px;
  color: var(--muted);
  user-select: none;
}

.diff-line__content {
  min-width: 0;
  padding: 1px 16px 1px 0;
}

.diff-line--add {
  border-left-color: var(--running);
  background: var(--diff-add);
}

.diff-line--add .diff-line__number {
  background: var(--diff-add);
}

.diff-line--add .diff-line__marker {
  color: var(--running);
}

.diff-line--remove {
  border-left-color: var(--error);
  background: var(--diff-remove);
}

.diff-line--remove .diff-line__number {
  background: var(--diff-remove);
}

.diff-line--remove .diff-line__marker {
  color: var(--error);
}

.diff-line--context {
  background: transparent;
}

.diff-expand {
  display: grid;
  grid-template-columns: 84px minmax(0, 1fr);
  min-width: max-content;
  border-block: 1px solid var(--border);
  background: color-mix(in srgb, var(--accent) 6%, var(--panel-bg));
  font-family: var(--font-mono-stack);
  font-size: 12px;
  line-height: 1.6;
}

.diff-expand__gutter {
  position: sticky;
  left: 0;
  z-index: 1;
  border-right: 1px solid var(--border);
  background: var(--diff-gutter);
}

.diff-expand__button {
  width: fit-content;
  margin: 0;
  border: 0;
  background: transparent;
  color: var(--accent);
  cursor: pointer;
  font: inherit;
  padding: 3px 12px;
  text-align: left;
}

.diff-expand__button:hover,
.diff-expand__button:focus-visible {
  text-decoration: underline;
}
</style>
