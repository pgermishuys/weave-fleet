import { autocompletion, closeBrackets, closeBracketsKeymap, completionKeymap } from "@codemirror/autocomplete";
import { defaultKeymap, history, historyKeymap, indentWithTab } from "@codemirror/commands";
import { bracketMatching, foldGutter, foldKeymap, indentOnInput } from "@codemirror/language";
import { highlightSelectionMatches, search, searchKeymap } from "@codemirror/search";
import { Compartment, EditorState, type Extension } from "@codemirror/state";
import {
  EditorView,
  drawSelection,
  dropCursor,
  highlightActiveLine,
  highlightActiveLineGutter,
  highlightSpecialChars,
  keymap,
  lineNumbers,
  rectangularSelection,
  type ViewUpdate,
} from "@codemirror/view";
import { agentLines } from "./agent-lines";
import { completionSources, type ListFolder } from "./completion";
import { fleetTheme } from "./theme";
import { textFormatExtension, type TextFormat } from "./text-format";

/** Swapped once the file's language has loaded. */
export const languageSlot = new Compartment();
/** Holds the merge view in Diff and Compare. */
export const mergeSlot = new Compartment();
/** Read-only while comparing a deleted file, for instance. */
export const readOnlySlot = new Compartment();

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

/** Everything a file's editor state carries. The callbacks are looked up at call time, so a remount can swap them. */
export function bufferExtensions(options: BufferExtensionOptions): Extension {
  return [
    agentLines,
    lineNumbers(),
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
    autocompletion({ icons: true }),
    completionSources(options.path, options.listFolder),
    keymap.of([
      { key: "Mod-s", preventDefault: true, run: () => (options.onSave(), true) },
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
