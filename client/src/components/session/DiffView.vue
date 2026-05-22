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
.diff-view {
  width: 100%;
  min-width: 0;
  overflow: auto;
  border-top: 1px solid rgba(255, 255, 255, 0.04);
  background: rgba(13, 17, 23, 0.38);
}

.diff-line {
  display: grid;
  grid-template-columns: 52px 52px 24px minmax(0, 1fr);
  align-items: start;
  min-width: max-content;
  border-left: 2px solid transparent;
  font-family: ui-monospace, SFMono-Regular, SFMono-Regular, Consolas, "Liberation Mono", Menlo, monospace;
  font-size: 11px;
  line-height: 1.55;
  color: #c9d1d9;
  white-space: pre;
}

.diff-line__number {
  position: sticky;
  z-index: 1;
  display: inline-block;
  min-height: 100%;
  padding: 2px 10px;
  border-right: 1px solid rgba(139, 148, 158, 0.18);
  background: rgba(13, 17, 23, 0.9);
  color: rgba(161, 161, 170, 0.8);
  text-align: right;
  user-select: none;
}

.diff-line__number--old {
  left: 0;
}

.diff-line__number--new {
  left: 52px;
}

.diff-line__marker {
  padding: 2px 8px;
  color: rgba(201, 209, 217, 0.78);
  user-select: none;
}

.diff-line__content {
  min-width: 0;
  padding: 2px 16px 2px 0;
}

.diff-line--add {
  border-left-color: rgba(46, 160, 67, 0.9);
  background: rgba(46, 160, 67, 0.18);
}

.diff-line--add .diff-line__number {
  background: rgba(46, 160, 67, 0.14);
}

.diff-line--add .diff-line__marker {
  color: #3fb950;
}

.diff-line--remove {
  border-left-color: rgba(248, 81, 73, 0.9);
  background: rgba(248, 81, 73, 0.16);
}

.diff-line--remove .diff-line__number {
  background: rgba(248, 81, 73, 0.12);
}

.diff-line--remove .diff-line__marker {
  color: #ff7b72;
}

.diff-line--context {
  background: rgba(13, 17, 23, 0.1);
}

.diff-expand {
  display: grid;
  grid-template-columns: 104px minmax(0, 1fr);
  min-width: max-content;
  border-block: 1px solid rgba(56, 139, 253, 0.14);
  background: rgba(56, 139, 253, 0.08);
  font-family: ui-monospace, SFMono-Regular, SFMono-Regular, Consolas, "Liberation Mono", Menlo, monospace;
  font-size: 11px;
  line-height: 1.55;
}

.diff-expand__gutter {
  position: sticky;
  left: 0;
  z-index: 1;
  border-right: 1px solid rgba(56, 139, 253, 0.18);
  background: rgba(13, 17, 23, 0.92);
}

.diff-expand__button {
  width: fit-content;
  margin: 0;
  border: 0;
  background: transparent;
  color: #79c0ff;
  cursor: pointer;
  font: inherit;
  padding: 3px 12px;
  text-align: left;
}

.diff-expand__button:hover,
.diff-expand__button:focus-visible {
  color: #a5d6ff;
  text-decoration: underline;
}
</style>
