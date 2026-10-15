import { Prec, RangeSetBuilder, StateEffect, StateField, type EditorState, type Extension } from "@codemirror/state";
import { Decoration, EditorView, GutterMarker, gutterLineClass, keymap, lineNumbers, type BlockInfo } from "@codemirror/view";

/**
 * Picking lines from the line numbers: click picks a line, shift-click or a drag down the numbers
 * picks a range. Picked lines aren't the text selection, so they stay through scrolling and a click
 * in the text; Esc, a new pick, or selecting text clears them.
 */

/** Where the pick started and where it ends, as the start of each line, so edits move them along. */
interface Pick {
  anchor: number;
  head: number;
}

/** First and last line, 1-based, first ≤ last. */
export interface LineRange {
  first: number;
  last: number;
}

const setPick = StateEffect.define<Pick | null>();

const pickField = StateField.define<Pick | null>({
  create: () => null,
  update(value, tr) {
    for (const effect of tr.effects) if (effect.is(setPick)) return effect.value;
    if (value && tr.isUserEvent("select") && !tr.newSelection.main.empty) return null;
    if (!value || !tr.docChanged) return value;
    return { anchor: tr.changes.mapPos(value.anchor), head: tr.changes.mapPos(value.head) };
  },
});

export function pickedLines(state: EditorState): LineRange | null {
  const pick = state.field(pickField, false);
  if (!pick) return null;
  const anchor = state.doc.lineAt(pick.anchor).number;
  const head = state.doc.lineAt(pick.head).number;
  return { first: Math.min(anchor, head), last: Math.max(anchor, head) };
}

/** The lines a non-empty text selection spans. One that ends at the start of a line doesn't include it. */
export function selectedLines(state: EditorState): LineRange | null {
  const selection = state.selection.main;
  if (selection.empty) return null;
  const first = state.doc.lineAt(selection.from).number;
  let last = state.doc.lineAt(selection.to).number;
  if (last > first && state.doc.lineAt(selection.to).from === selection.to) last--;
  return { first, last };
}

export function pickLines(view: EditorView, anchorLine: number, headLine = anchorLine): void {
  const doc = view.state.doc;
  view.dispatch({ effects: setPick.of({ anchor: doc.line(anchorLine).from, head: doc.line(headLine).from }) });
}

export function clearPick(view: EditorView): boolean {
  if (!view.state.field(pickField, false)) return false;
  view.dispatch({ effects: setPick.of(null) });
  return true;
}

const pickedLine = Decoration.line({ class: "cm-picked-line" });

class PickedGutter extends GutterMarker {
  override elementClass = "cm-picked-gutter";
}
const pickedGutter = new PickedGutter();

function eachPickedLine(state: EditorState, add: (from: number) => void): void {
  const range = pickedLines(state);
  if (!range) return;
  for (let number = range.first; number <= range.last; number++) add(state.doc.line(number).from);
}

const pickDecorations = EditorView.decorations.compute([pickField], (state) => {
  const builder = new RangeSetBuilder<Decoration>();
  eachPickedLine(state, (from) => builder.add(from, from, pickedLine));
  return builder.finish();
});

const pickGutterClasses = gutterLineClass.compute([pickField], (state) => {
  const builder = new RangeSetBuilder<GutterMarker>();
  eachPickedLine(state, (from) => builder.add(from, from, pickedGutter));
  return builder.finish();
});

function onNumberMouseDown(view: EditorView, line: BlockInfo, event: Event): boolean {
  const mouse = event as MouseEvent;
  if (mouse.button !== 0) return false;
  mouse.preventDefault();
  const doc = view.state.doc;
  const current = view.state.field(pickField);
  let head = doc.lineAt(line.from).number;
  // Shift-click extends from where the last pick started.
  const start = mouse.shiftKey && current ? doc.lineAt(current.anchor).number : head;
  pickLines(view, start, head);
  // So Esc reaches the editor; the cursor stays where it was.
  view.focus();

  const move = (next: MouseEvent) => {
    const block = view.lineBlockAtHeight(next.clientY - view.documentTop);
    const over = view.state.doc.lineAt(block.from).number;
    if (over === head) return;
    head = over;
    pickLines(view, start, head);
  };
  const up = () => {
    window.removeEventListener("mousemove", move);
    window.removeEventListener("mouseup", up);
  };
  window.addEventListener("mousemove", move);
  window.addEventListener("mouseup", up);
  return true;
}

/** Line numbers that pick lines, plus the picks' state, highlight and Esc. Use in place of `lineNumbers()`. */
export const linePicking: Extension = [
  pickField,
  pickDecorations,
  pickGutterClasses,
  lineNumbers({ domEventHandlers: { mousedown: onNumberMouseDown } }),
  Prec.high(keymap.of([{ key: "Escape", run: clearPick }])),
];
