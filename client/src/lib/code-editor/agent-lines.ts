import { diff } from "@codemirror/merge";
import { RangeSet, StateEffect, StateField, Text, type EditorState, type Extension } from "@codemirror/state";
import { Decoration, EditorView, GutterMarker, gutter, type DecorationSet } from "@codemirror/view";

/**
 * Two small editor extensions. A thin accent stripe in the gutter marks lines that differ from the
 * git base (what the Changes canvas shows), and lines the agent just changed flash once.
 */

class ChangedLineMarker extends GutterMarker {
  override toDOM(): HTMLElement {
    const el = document.createElement("div");
    el.className = "cm-agent-mark";
    return el;
  }
}

const changedLineMarker = new ChangedLineMarker();
const setChangedLines = StateEffect.define<RangeSet<GutterMarker>>();

const changedLinesField = StateField.define<RangeSet<GutterMarker>>({
  create: () => RangeSet.empty,
  update(value, tr) {
    let next = value.map(tr.changes);
    for (const effect of tr.effects) if (effect.is(setChangedLines)) next = effect.value;
    return next;
  },
});

const flashLine = Decoration.line({ class: "cm-agent-flash" });
const setFlash = StateEffect.define<DecorationSet>();

const flashField = StateField.define<DecorationSet>({
  create: () => Decoration.none,
  update(value, tr) {
    let next = value.map(tr.changes);
    for (const effect of tr.effects) if (effect.is(setFlash)) next = effect.value;
    return next;
  },
  provide: (field) => EditorView.decorations.from(field),
});

export const agentLines: Extension = [
  changedLinesField,
  flashField,
  gutter({ class: "cm-agent-gutter", markers: (view) => view.state.field(changedLinesField) }),
];

/** The git base as editor text: line breaks don't matter, and a BOM isn't content. */
export function baseText(before: string): Text {
  return Text.of(before.replace(/^\uFEFF/, "").split(/\r\n?|\n/));
}

/** Each distinct line as one character, so a character diff is a line diff. */
function encodeLines(text: Text, ids: Map<string, string>): string | null {
  let out = "";
  for (const iter = text.iterLines(); !iter.next().done;) {
    let id = ids.get(iter.value);
    if (id === undefined) {
      const code = 0x100 + ids.size;
      // Past the surrogate range, and out of characters: too many distinct lines to mark.
      const skipped = code >= 0xd800 ? code + 0x800 : code;
      if (skipped > 0xffff) return null;
      id = String.fromCharCode(skipped);
      ids.set(iter.value, id);
    }
    out += id;
  }
  return out;
}

/** 1-based lines of `doc` that differ from `base`. A deletion marks the line after it. */
export function changedLines(base: Text, doc: Text): number[] {
  const ids = new Map<string, string>();
  const a = encodeLines(base, ids);
  const b = encodeLines(doc, ids);
  if (a === null || b === null) return [];

  const lines = new Set<number>();
  for (const change of diff(a, b)) {
    if (change.toB > change.fromB) {
      for (let index = change.fromB; index < change.toB; index++) lines.add(index + 1);
    } else {
      lines.add(Math.min(change.fromB + 1, doc.lines));
    }
  }
  return [...lines].sort((x, y) => x - y);
}

function lineStarts(doc: Text, lines: readonly number[]): number[] {
  return lines.filter((n) => n >= 1 && n <= doc.lines).map((n) => doc.line(n).from);
}

/** Effects that mark `lines` in the gutter (replacing earlier marks). */
export function markChangedLines(doc: Text, lines: readonly number[]): StateEffect<unknown> {
  return setChangedLines.of(RangeSet.of(lineStarts(doc, lines).map((from) => changedLineMarker.range(from)), true));
}

/** Effects that flash `lines` once. */
export function flashLines(doc: Text, lines: readonly number[]): StateEffect<unknown> {
  return setFlash.of(Decoration.set(lineStarts(doc, lines).map((from) => flashLine.range(from)), true));
}

/** Lines marked in the gutter, for tests. */
export function markedLines(state: EditorState): number[] {
  const result: number[] = [];
  for (const cursor = state.field(changedLinesField).iter(); cursor.value; cursor.next()) {
    result.push(state.doc.lineAt(cursor.from).number);
  }
  return result;
}
