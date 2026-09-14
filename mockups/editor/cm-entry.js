import { EditorState, StateField, StateEffect, RangeSet, Compartment } from "@codemirror/state";
import {
  EditorView, keymap, lineNumbers, highlightActiveLineGutter, highlightSpecialChars, drawSelection,
  dropCursor, rectangularSelection, highlightActiveLine, gutter, GutterMarker, Decoration,
} from "@codemirror/view";
import {
  indentOnInput, syntaxHighlighting, bracketMatching, foldGutter, foldKeymap, HighlightStyle,
} from "@codemirror/language";
import { history, defaultKeymap, historyKeymap, indentWithTab } from "@codemirror/commands";
import { searchKeymap, highlightSelectionMatches } from "@codemirror/search";
import {
  autocompletion, completionKeymap, closeBrackets, closeBracketsKeymap, completeAnyWord,
} from "@codemirror/autocomplete";
import { javascript, javascriptLanguage } from "@codemirror/lang-javascript";
import { json } from "@codemirror/lang-json";
import { markdown } from "@codemirror/lang-markdown";
import { css } from "@codemirror/lang-css";
import { html } from "@codemirror/lang-html";
import { unifiedMergeView, MergeView } from "@codemirror/merge";
import { tags as t } from "@lezer/highlight";

// Colours come from CSS variables so the editor follows Fleet's theme without a reconfigure.
const fleetHighlight = HighlightStyle.define([
  { tag: [t.keyword, t.modifier, t.controlKeyword, t.operatorKeyword], color: "var(--syn-keyword)" },
  { tag: [t.string, t.special(t.string), t.regexp], color: "var(--syn-string)" },
  { tag: [t.number, t.bool, t.null, t.atom], color: "var(--syn-number)" },
  { tag: [t.comment, t.lineComment, t.blockComment], color: "var(--syn-comment)", fontStyle: "italic" },
  { tag: [t.function(t.variableName), t.function(t.propertyName)], color: "var(--syn-fn)" },
  { tag: [t.typeName, t.className, t.namespace], color: "var(--syn-type)" },
  { tag: [t.propertyName], color: "var(--syn-prop)" },
  { tag: [t.definition(t.variableName)], color: "var(--syn-def)" },
  { tag: [t.heading], color: "var(--syn-keyword)", fontWeight: "600" },
  { tag: [t.link, t.url], color: "var(--syn-string)", textDecoration: "underline" },
  { tag: [t.punctuation, t.bracket], color: "var(--syn-punct)" },
  { tag: [t.tagName], color: "var(--syn-keyword)" },
  { tag: [t.attributeName], color: "var(--syn-prop)" },
]);

const fleetTheme = EditorView.theme({
  "&": { color: "var(--text)", backgroundColor: "transparent", fontSize: "12.5px", height: "100%" },
  ".cm-scroller": { fontFamily: "var(--font-mono-stack)", lineHeight: "1.6" },
  ".cm-content": { caretColor: "var(--accent)", padding: "6px 0" },
  ".cm-cursor, .cm-dropCursor": { borderLeftColor: "var(--accent)", borderLeftWidth: "2px" },
  "&.cm-focused .cm-selectionBackground, .cm-selectionBackground, ::selection": {
    backgroundColor: "var(--sel) !important",
  },
  ".cm-gutters": { backgroundColor: "transparent", color: "var(--gutter)", border: "none" },
  ".cm-lineNumbers .cm-gutterElement": { padding: "0 10px 0 8px", minWidth: "34px" },
  ".cm-activeLine": { backgroundColor: "var(--active-line)" },
  ".cm-activeLineGutter": { backgroundColor: "transparent", color: "var(--text)" },
  ".cm-foldGutter .cm-gutterElement": { color: "var(--gutter)", padding: "0 2px" },
  ".cm-matchingBracket": { backgroundColor: "var(--bracket)", outline: "none" },
  ".cm-selectionMatch": { backgroundColor: "var(--sel-match)" },
  ".cm-tooltip": {
    border: "1px solid var(--border)", backgroundColor: "var(--card-bg)", borderRadius: "8px",
    boxShadow: "var(--pop-shadow)", overflow: "hidden",
  },
  ".cm-tooltip-autocomplete > ul": { fontFamily: "var(--font-mono-stack)", fontSize: "12px", maxHeight: "12em", padding: "4px" },
  ".cm-tooltip-autocomplete > ul > li": { borderRadius: "5px", padding: "2px 8px 2px 4px !important", lineHeight: "1.7" },
  ".cm-tooltip-autocomplete > ul > li[aria-selected]": { backgroundColor: "var(--accent-dim)", color: "var(--text)" },
  ".cm-completionDetail": { color: "var(--muted)", fontStyle: "normal", marginLeft: "10px", fontFamily: "var(--font-sans-stack)", fontSize: "11px" },
  ".cm-completionMatchedText": { textDecoration: "none", color: "var(--accent)", fontWeight: "600" },
  ".cm-completionIcon": { opacity: "0.7", width: "1.1em", paddingRight: "0.9em" },
  ".cm-panels": { backgroundColor: "var(--card-bg)", color: "var(--text)" },
  ".cm-panels.cm-panels-bottom": { borderTop: "1px solid var(--border)" },
  ".cm-search": { fontFamily: "var(--font-sans-stack)", fontSize: "12px" },
  ".cm-textfield": { border: "1px solid var(--border)", borderRadius: "5px", background: "transparent", color: "var(--text)" },
  ".cm-button": { backgroundImage: "none", background: "var(--chip)", border: "1px solid var(--border)", borderRadius: "5px", color: "var(--text)" },
  ".cm-agent-gutter": { width: "3px", marginRight: "2px" },
  ".cm-agent-mark": { width: "3px", height: "100%", background: "var(--agent)", borderRadius: "2px" },
  ".cm-agent-flash": { animation: "cm-agent-flash 1.6s ease-out" },
  ".cm-foldPlaceholder": { background: "var(--chip)", border: "none", color: "var(--muted)" },
  // merge view
  ".cm-changedLine": { backgroundColor: "var(--diff-add-bg) !important" },
  ".cm-deletedChunk": { backgroundColor: "var(--diff-del-bg) !important" },
  ".cm-changedText": { background: "var(--diff-add-strong) !important" },
  ".cm-deletedChunk .cm-deletedText, del": { background: "var(--diff-del-strong) !important", textDecoration: "none" },
  ".cm-chunkButtons button": { fontFamily: "var(--font-sans-stack)", fontSize: "11px", border: "1px solid var(--border)", borderRadius: "5px", background: "var(--card-bg)", color: "var(--text)", padding: "1px 8px", cursor: "pointer" },
  ".cm-merge-revert": { background: "var(--chip)" },
});

// Lines the agent changed this session: a thin stripe in its own gutter.
class AgentMarker extends GutterMarker {
  toDOM() { const el = document.createElement("div"); el.className = "cm-agent-mark"; return el; }
}
const agentMarker = new AgentMarker();
const setAgentLines = StateEffect.define();
const agentLines = StateField.define({
  create: () => RangeSet.empty,
  update(value, tr) {
    value = value.map(tr.changes);
    for (const e of tr.effects) if (e.is(setAgentLines)) value = e.value;
    return value;
  },
});
const flashMark = Decoration.line({ class: "cm-agent-flash" });
const setFlash = StateEffect.define();
const flashField = StateField.define({
  create: () => Decoration.none,
  update(value, tr) {
    value = value.map(tr.changes);
    for (const e of tr.effects) if (e.is(setFlash)) value = e.value;
    return value;
  },
  provide: (f) => EditorView.decorations.from(f),
});
const agentGutter = [
  agentLines,
  flashField,
  gutter({ class: "cm-agent-gutter", markers: (v) => v.state.field(agentLines) }),
];

function langFor(path) {
  if (/\.(ts|tsx|mts)$/.test(path)) return javascript({ typescript: true, jsx: path.endsWith("x") });
  if (/\.(js|jsx|mjs|cjs|vue)$/.test(path)) return javascript({ jsx: path.endsWith("x") });
  if (/\.json$/.test(path)) return json();
  if (/\.(md|markdown)$/.test(path)) return markdown();
  if (/\.(css|scss)$/.test(path)) return css();
  if (/\.html?$/.test(path)) return html();
  return [];
}

// Workspace-aware completion: import specifiers complete from the repo's file list.
function importPathSource(files) {
  return (ctx) => {
    const before = ctx.matchBefore(/(?:from\s+|import\s*\(\s*)["'][^"']*/);
    if (!before) return null;
    const quote = before.text.search(/["']/);
    const typed = before.text.slice(quote + 1);
    const from = before.from + quote + 1;
    return {
      from,
      options: files.map((f) => ({ label: f, type: "file", detail: "in this repo", boost: f.startsWith(typed) ? 1 : 0 })),
      validFor: /^[\w@./-]*$/,
    };
  };
}

function basics() {
  return [
    lineNumbers(), highlightActiveLineGutter(), highlightSpecialChars(), history(), foldGutter(),
    drawSelection(), dropCursor(), EditorState.allowMultipleSelections.of(true), indentOnInput(),
    syntaxHighlighting(fleetHighlight), bracketMatching(), closeBrackets(), rectangularSelection(),
    highlightActiveLine(), highlightSelectionMatches(),
  ];
}

export function createEditor(parent, opts) {
  const readOnly = new Compartment();
  const extensions = [
    agentGutter,
    basics(),
    fleetTheme,
    langFor(opts.path || ""),
    readOnly.of(EditorState.readOnly.of(!!opts.readOnly)),
    autocompletion({
      icons: true,
      override: opts.completion === false ? [] : undefined,
    }),
    opts.importFiles ? javascriptLanguage.data.of({ autocomplete: importPathSource(opts.importFiles) }) : [],
    EditorState.languageData.of(() => [{ autocomplete: completeAnyWord }]),
    keymap.of([
      { key: "Mod-s", preventDefault: true, run: () => { opts.onSave && opts.onSave(); return true; } },
      ...closeBracketsKeymap, ...defaultKeymap, ...searchKeymap, ...historyKeymap, ...foldKeymap,
      ...completionKeymap, indentWithTab,
    ]),
    EditorView.updateListener.of((u) => {
      if (u.docChanged && opts.onChange) opts.onChange(u);
      if (u.selectionSet && opts.onSelection) opts.onSelection(u.view);
    }),
    EditorView.domEventHandlers({ blur: (e, v) => { opts.onBlur && opts.onBlur(v); return false; } }),
  ];
  const view = new EditorView({ parent, state: EditorState.create({ doc: opts.doc, extensions }) });
  view._fleet = { readOnly };
  return view;
}

export function setReadOnly(view, value) {
  view.dispatch({ effects: view._fleet.readOnly.reconfigure(EditorState.readOnly.of(value)) });
}

// Mark lines (1-based) as agent-changed, optionally flash them.
export function markAgentLines(view, lines, flashLines) {
  const doc = view.state.doc;
  const ok = (n) => n >= 1 && n <= doc.lines;
  const ranges = lines.filter(ok).map((n) => agentMarker.range(doc.line(n).from));
  const effects = [setAgentLines.of(RangeSet.of(ranges, true))];
  if (flashLines && flashLines.length) {
    effects.push(setFlash.of(Decoration.set(flashLines.filter(ok).map((n) => flashMark.range(doc.line(n).from)), true)));
    const first = doc.line(flashLines.filter(ok)[0] || 1);
    effects.push(EditorView.scrollIntoView(first.from, { y: "center" }));
  }
  view.dispatch({ effects });
}

// Replace only the part of the document that differs, so the cursor and scroll mostly stay put.
export function setDocMinimal(view, text) {
  const old = view.state.doc.toString();
  if (old === text) return;
  let p = 0;
  while (p < old.length && p < text.length && old[p] === text[p]) p++;
  let s = 0;
  while (s < old.length - p && s < text.length - p && old[old.length - 1 - s] === text[text.length - 1 - s]) s++;
  view.dispatch({ changes: { from: p, to: old.length - s, insert: text.slice(p, text.length - s) } });
}

export function replaceDoc(view, text) {
  view.dispatch({ changes: { from: 0, to: view.state.doc.length, insert: text } });
}

export function createUnifiedDiff(parent, opts) {
  return new EditorView({
    parent,
    state: EditorState.create({
      doc: opts.after,
      extensions: [
        basics(), fleetTheme, langFor(opts.path || ""), autocompletion(),
        keymap.of([...defaultKeymap, ...historyKeymap, ...completionKeymap]),
        unifiedMergeView({ original: opts.before, mergeControls: true, highlightChanges: true, gutter: true }),
        EditorView.updateListener.of((u) => { if (u.docChanged && opts.onChange) opts.onChange(u); }),
      ],
    }),
  });
}

export function createSideBySide(parent, opts) {
  const ext = [basics(), fleetTheme, langFor(opts.path || "")];
  return new MergeView({
    parent,
    a: { doc: opts.before, extensions: [...ext, EditorState.readOnly.of(true)] },
    b: { doc: opts.after, extensions: [...ext, keymap.of([...defaultKeymap, ...historyKeymap])] },
    highlightChanges: true,
    gutter: true,
  });
}

export { EditorView };
