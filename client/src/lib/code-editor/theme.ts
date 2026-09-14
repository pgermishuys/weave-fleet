import { HighlightStyle, syntaxHighlighting } from "@codemirror/language";
import type { Extension } from "@codemirror/state";
import { EditorView } from "@codemirror/view";
import { tags as t } from "@lezer/highlight";

/**
 * Fleet's editor theme. Every colour is a CSS variable: syntax from the `--syntax-*` tokens that
 * code blocks in Markdown use, chrome from the theme's text, muted, border and accent. So the
 * editor follows all of Fleet's themes, and a theme switch needs no reconfigure.
 */
const fleetHighlight = HighlightStyle.define([
  { tag: [t.keyword, t.modifier, t.controlKeyword, t.operatorKeyword, t.definitionKeyword, t.moduleKeyword], color: "var(--syntax-keyword)" },
  { tag: [t.string, t.special(t.string), t.regexp, t.character], color: "var(--syntax-string)" },
  { tag: [t.number, t.integer, t.float], color: "var(--syntax-number)" },
  { tag: [t.bool, t.null, t.atom, t.self, t.special(t.variableName)], color: "var(--syntax-literal)" },
  { tag: [t.comment, t.lineComment, t.blockComment, t.docComment], color: "var(--syntax-comment)", fontStyle: "italic" },
  { tag: [t.meta, t.processingInstruction, t.annotation], color: "var(--syntax-comment)" },
  { tag: [t.function(t.variableName), t.function(t.propertyName), t.function(t.definition(t.variableName))], color: "var(--syntax-function)" },
  { tag: [t.typeName, t.className, t.namespace, t.tagName, t.standard(t.variableName)], color: "var(--syntax-type)" },
  { tag: [t.propertyName, t.attributeName, t.labelName], color: "var(--syntax-attr)" },
  { tag: [t.escape, t.color, t.unit], color: "var(--syntax-literal)" },
  { tag: [t.link, t.url], color: "var(--syntax-literal)", textDecoration: "underline" },
  { tag: [t.heading], color: "var(--md-heading)", fontWeight: "600" },
  { tag: [t.strong], fontWeight: "600" },
  { tag: [t.emphasis], fontStyle: "italic" },
  { tag: [t.strikethrough], textDecoration: "line-through" },
  { tag: [t.punctuation, t.bracket, t.separator, t.operator], color: "var(--muted)" },
  { tag: [t.invalid], color: "var(--error)" },
  { tag: [t.inserted], color: "var(--running)" },
  { tag: [t.deleted], color: "var(--error)" },
]);

const mix = (color: string, percent: number) => `color-mix(in srgb, ${color} ${percent}%, transparent)`;

const fleetChrome = EditorView.theme({
  "&": { color: "var(--text)", backgroundColor: "transparent", fontSize: "12.5px", height: "100%" },
  "&.cm-focused": { outline: "none" },
  ".cm-scroller": { fontFamily: "var(--font-mono-stack)", lineHeight: "1.6" },
  ".cm-content": { caretColor: "var(--accent)", padding: "6px 0" },
  ".cm-cursor, .cm-dropCursor": { borderLeftColor: "var(--accent)", borderLeftWidth: "2px" },
  "&.cm-focused > .cm-scroller > .cm-selectionLayer .cm-selectionBackground, .cm-selectionBackground, .cm-content ::selection": {
    backgroundColor: `${mix("var(--accent)", 30)} !important`,
  },
  ".cm-gutters": { backgroundColor: "transparent", color: mix("var(--muted)", 70), border: "none" },
  ".cm-lineNumbers .cm-gutterElement": { padding: "0 10px 0 8px", minWidth: "34px" },
  ".cm-activeLine": { backgroundColor: mix("var(--text)", 3) },
  ".cm-activeLineGutter": { backgroundColor: "transparent", color: "var(--text)" },
  ".cm-foldGutter .cm-gutterElement": { color: mix("var(--muted)", 70), padding: "0 2px" },
  ".cm-foldPlaceholder": { background: mix("var(--text)", 6), border: "none", color: "var(--muted)", padding: "0 6px", borderRadius: "4px" },
  ".cm-matchingBracket, &.cm-focused .cm-matchingBracket": { backgroundColor: mix("var(--accent)", 25), outline: "none" },
  ".cm-nonmatchingBracket": { color: "var(--error)" },
  ".cm-selectionMatch": { backgroundColor: mix("var(--accent)", 14) },
  ".cm-searchMatch": { backgroundColor: mix("var(--idle)", 25), outline: `1px solid ${mix("var(--idle)", 45)}` },
  ".cm-searchMatch.cm-searchMatch-selected": { backgroundColor: mix("var(--idle)", 45) },
  ".cm-specialChar": { color: "var(--error)" },
  ".cm-tooltip": {
    border: "1px solid var(--border)",
    backgroundColor: "var(--card-bg)",
    color: "var(--text)",
    borderRadius: "8px",
    boxShadow: "0 16px 40px -12px rgba(0, 0, 0, 0.45)",
    overflow: "hidden",
  },
  ".cm-tooltip-autocomplete > ul": { fontFamily: "var(--font-mono-stack)", fontSize: "12px", maxHeight: "14em", padding: "4px" },
  ".cm-tooltip-autocomplete > ul > li": { borderRadius: "5px", padding: "2px 8px 2px 4px !important", lineHeight: "1.7" },
  ".cm-tooltip-autocomplete > ul > li[aria-selected]": { backgroundColor: "var(--accent-dim)", color: "var(--text)" },
  ".cm-completionDetail": { color: "var(--muted)", fontStyle: "normal", marginLeft: "10px", fontFamily: "var(--font-sans-stack)", fontSize: "11px" },
  ".cm-completionMatchedText": { textDecoration: "none", color: "var(--accent)", fontWeight: "600" },
  ".cm-completionIcon": { opacity: "0.7", width: "1.1em", paddingRight: "0.9em" },
  ".cm-panels": { backgroundColor: "var(--card-bg)", color: "var(--text)" },
  ".cm-panels.cm-panels-top": { borderBottom: "1px solid var(--border)" },
  ".cm-panels.cm-panels-bottom": { borderTop: "1px solid var(--border)" },
  ".cm-search": { fontFamily: "var(--font-sans-stack)", fontSize: "12px", padding: "6px 8px" },
  ".cm-search label": { color: "var(--muted)" },
  ".cm-textfield": {
    border: "1px solid var(--border)", borderRadius: "5px", background: "transparent", color: "var(--text)", fontSize: "12px",
  },
  ".cm-button": {
    backgroundImage: "none", background: mix("var(--text)", 6), border: "1px solid var(--border)", borderRadius: "5px", color: "var(--text)",
  },
  // Lines that differ from the git base: a thin accent stripe in its own gutter.
  ".cm-agent-gutter": { width: "3px", marginRight: "2px" },
  ".cm-agent-gutter .cm-gutterElement": { padding: "0" },
  ".cm-agent-mark": { width: "3px", height: "100%", background: "var(--accent)", borderRadius: "2px" },
  ".cm-agent-flash": { animation: "cm-agent-flash 1.6s ease-out" },
  // The merge views: Diff (git base against the buffer) and Compare (the agent's version against yours).
  ".cm-changedLine, .cm-insertedLine": { backgroundColor: `${mix("var(--running)", 10)} !important` },
  ".cm-deletedChunk, .cm-deletedLine": { backgroundColor: `${mix("var(--error)", 10)} !important` },
  ".cm-changedText, .cm-insertedText": { background: `${mix("var(--running)", 25)} !important` },
  ".cm-deletedChunk .cm-deletedText, .cm-deletedChunk del": { background: `${mix("var(--error)", 28)} !important`, textDecoration: "none" },
  ".cm-changeGutter": { width: "3px", paddingLeft: "0" },
  ".cm-changedLineGutter": { background: "var(--running)" },
  ".cm-deletedLineGutter": { background: "var(--error)" },
  ".cm-chunkButtons": { position: "absolute", insetInlineEnd: "8px" },
  ".cm-chunkButtons button": {
    fontFamily: "var(--font-sans-stack)", fontSize: "11px", border: "1px solid var(--border)", borderRadius: "5px",
    background: "var(--card-bg)", color: "var(--text)", padding: "1px 8px", marginLeft: "4px", cursor: "pointer",
  },
  ".cm-chunkButtons button:hover": { background: mix("var(--text)", 8) },
  "@keyframes cm-agent-flash": { from: { backgroundColor: mix("var(--accent)", 28) }, to: { backgroundColor: "transparent" } },
});

export const fleetTheme: Extension = [fleetChrome, syntaxHighlighting(fleetHighlight)];
