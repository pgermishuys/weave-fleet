import { autocompletion, closeBrackets, closeBracketsKeymap, completionKeymap } from "@codemirror/autocomplete";
import { defaultKeymap, history, historyKeymap, indentWithTab } from "@codemirror/commands";
import { bracketMatching, foldGutter, foldKeymap, indentOnInput } from "@codemirror/language";
import { highlightSelectionMatches, search, searchKeymap } from "@codemirror/search";
import { Compartment, EditorState, Facet, RangeSetBuilder, countColumn, type Extension } from "@codemirror/state";
import {
  Decoration,
  EditorView,
  ViewPlugin,
  drawSelection,
  dropCursor,
  highlightActiveLine,
  highlightActiveLineGutter,
  highlightSpecialChars,
  keymap,
  rectangularSelection,
  type Command,
  type DecorationSet,
  type ViewUpdate,
} from "@codemirror/view";
import { agentLines } from "./agent-lines";
import { completionSources, type ListFolder } from "./completion";
import { linePicking } from "./line-pick";
import { fleetTheme } from "./theme";
import { textFormatExtension, type TextFormat } from "./text-format";
import { setWrapFor, wrapFor } from "./wrap-preference";

/** Swapped once the file's language has loaded. */
export const languageSlot = new Compartment();
/** Holds the merge view in Diff and Compare. */
export const mergeSlot = new Compartment();
/** Read-only while comparing a deleted file, for instance. */
export const readOnlySlot = new Compartment();
/** Soft wrap, on or off per file. */
export const wrapSlot = new Compartment();

export interface BufferExtensionOptions {
  path: string;
  format: TextFormat;
  language: Extension | null;
  listFolder: ListFolder | null;
  onSave: () => void;
  onUpdate: (update: ViewUpdate) => void;
}

export function readOnly(value: boolean): Extension {
  return value ? [EditorState.readOnly.of(true), EditorView.editable.of(false)] : [];
}

// ─── Soft wrap ───────────────────────────────────────────────────────────────

const wrapped = Facet.define<boolean, boolean>({ combine: (values) => values.some(Boolean) });
const hangs = new Map<number, Decoration>();

function hang(columns: number): Decoration {
  let decoration = hangs.get(columns);
  if (!decoration) {
    // The first row starts where it always did; the rows it wraps onto start under its indent, plus two.
    decoration = Decoration.line({ attributes: { style: `padding-left: calc(${columns}ch + 6px); text-indent: -${columns}ch` } });
    hangs.set(columns, decoration);
  }
  return decoration;
}

function hangingIndents(view: EditorView): DecorationSet {
  const builder = new RangeSetBuilder<Decoration>();
  const { doc, tabSize } = view.state;
  let next = 0;
  for (const { from, to } of view.visibleRanges) {
    for (let pos = Math.max(from, next); pos <= to; pos = next) {
      const line = doc.lineAt(pos);
      const indent = countColumn(/^[ \t]*/.exec(line.text)![0], tabSize);
      // A deeply indented line in a narrow panel still keeps some room for its text.
      builder.add(line.from, line.from, hang(Math.min(indent + 2, 24)));
      next = line.to + 1;
    }
  }
  return builder.finish();
}

const hangingIndent = ViewPlugin.fromClass(class {
  decorations: DecorationSet;
  constructor(view: EditorView) {
    this.decorations = hangingIndents(view);
  }
  update(update: ViewUpdate) {
    if (update.docChanged || update.viewportChanged) this.decorations = hangingIndents(update.view);
  }
}, { decorations: (plugin) => plugin.decorations });

export function wrapping(on: boolean): Extension {
  return on ? [EditorView.lineWrapping, hangingIndent, wrapped.of(true)] : [];
}

export function isWrapped(state: EditorState): boolean {
  return state.facet(wrapped);
}

/** Turn wrap on or off for a file's editor and remember it for the file. */
export function setWrap(view: EditorView, path: string, on: boolean): void {
  setWrapFor(path, on);
  view.dispatch({ effects: wrapSlot.reconfigure(wrapping(on)) });
}

function toggleWrap(path: string): Command {
  return (view) => (setWrap(view, path, !isWrapped(view.state)), true);
}

/** Everything a file's editor state carries. The callbacks are looked up at call time, so a remount can swap them. */
export function bufferExtensions(options: BufferExtensionOptions): Extension {
  return [
    agentLines,
    linePicking,
    highlightActiveLineGutter(),
    highlightSpecialChars(),
    history(),
    foldGutter(),
    drawSelection(),
    dropCursor(),
    EditorState.allowMultipleSelections.of(true),
    indentOnInput(),
    bracketMatching(),
    closeBrackets(),
    rectangularSelection(),
    highlightActiveLine(),
    highlightSelectionMatches(),
    search({ top: true }),
    fleetTheme,
    textFormatExtension(options.format),
    languageSlot.of(options.language ?? []),
    readOnlySlot.of([]),
    mergeSlot.of([]),
    wrapSlot.of(wrapping(wrapFor(options.path))),
    autocompletion({ icons: true }),
    completionSources(options.path, options.listFolder),
    keymap.of([
      { key: "Mod-s", preventDefault: true, run: () => (options.onSave(), true) },
      { key: "Alt-z", preventDefault: true, run: toggleWrap(options.path) },
      ...closeBracketsKeymap,
      ...defaultKeymap,
      ...searchKeymap,
      ...historyKeymap,
      ...foldKeymap,
      ...completionKeymap,
      indentWithTab,
    ]),
    EditorView.updateListener.of((update) => options.onUpdate(update)),
  ];
}
