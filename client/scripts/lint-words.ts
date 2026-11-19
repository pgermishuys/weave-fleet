#!/usr/bin/env node
// Words lint, beside the design lint (lint-design-system.ts). It reads the words people see, not the
// code: template text and string props (title, label, placeholder, aria-label, …) in client/src's .vue and .tsx files,
// copy keys (title:, label:, body:, message:, …), toasts, notices and error messages in their scripts and .ts files, the
// same in the phone app (mobile/src), and the sentences the server sends (string literals in src/ that read as a
// sentence). Tests, comments and identifiers aren't read. Four rules:
//
//   MACHINE_WORD  "computer", "node(s)" or "Fleets" for a machine. Fleet says "machine". Allowed: `fleet node` (the
//                 command), Node.js, and "computer" for the desk as against the phone ("Open on my computer")
//   THREE_DOTS    "..." where "…" is meant ("Loading…", "Search sessions…")
//   RETRY_LABEL   a button or link that says "Retry". Fleet says "Try again"; a status like "Retry 2/3" is fine
//   TITLE_CASE    a Title Case label on a button, menu item, tab or heading, or in a label/title prop or key ("New
//                 Project"). Fleet writes labels in sentence case ("New project"). Proper nouns, rail and Settings
//                 page names (Sessions, Labs, Memory, …) and theme names don't count
//
// The server's sentences only count for MACHINE_WORD and THREE_DOTS: which of them reach a button can't be told.
//
// It's a gate, not a ratchet: the words passes (#991, #998, #1001) and the few fixes that came with this check left
// every count at 0, so any hit fails.
//
//   node scripts/lint-words.ts                     check (part of `lint`)
//   node scripts/lint-words.ts --list TITLE_CASE   print every hit of one rule (no rule: all of them)
import { existsSync, readdirSync, readFileSync } from "node:fs";
import { join, relative, sep } from "node:path";
import { pathToFileURL } from "node:url";

export const RULES = ["MACHINE_WORD", "THREE_DOTS", "RETRY_LABEL", "TITLE_CASE"] as const;
export type Rule = (typeof RULES)[number];

export interface Violation {
  rule: Rule;
  file: string;
  line: number;
  detail: string;
}

/** Where a string sits: a label-like place (button, menu item, tab, heading, label prop or key) or plain copy. */
export type Place = "label" | "copy";

export interface Phrase {
  file: string;
  line: number;
  text: string;
  place: Place;
  /** The server's sentences: only the word rules apply. */
  server?: boolean;
}

// ─── Extracting the words ────────────────────────────────────────────────────────────────────────────────────────────

// Elements whose own words are a label. Lower-cased, so `DropdownMenuItem` and `dropdown-menu-item` both match.
const LABEL_TAGS = new Set([
  "button", "a", "routerlink", "alertdialogaction", "alertdialogcancel", "dialogclose", "sheetclose", "popoverclose",
  "dropdownmenuitem", "dropdownmenulabel", "dropdownmenucheckboxitem", "dropdownmenuradioitem", "dropdownmenusubtrigger",
  "contextmenuitem", "contextmenucheckboxitem", "contextmenusubtrigger", "commanditem", "menuitem", "selectitem",
  "selectlabel", "tabstrigger", "h1", "h2", "h3", "h4", "h5", "h6", "dialogtitle", "alertdialogtitle", "sheettitle",
  "cardtitle", "responsivedialogtitle", "popovertitle", "label", "pressable", "touchableopacity",
].map((t) => t.replace(/-/g, "")));
// Elements that sit inside a label without ending it (`<Button><span>New project</span></Button>`).
const INLINE_TAGS = new Set(["span", "strong", "em", "b", "i", "kbd", "small", "u", "template", "slot", "text", "component"]);
const VOID_TAGS = new Set(["br", "hr", "img", "input", "meta", "link", "source", "wbr", "col"]);

// Props whose value is words someone reads. The label-like ones are checked for Title Case too.
const LABEL_PROPS = new Set(["label", "title", "aria-label", "arialabel", "accessibilitylabel", "confirm-label", "confirmlabel",
  "cancel-label", "cancellabel", "heading", "action-label", "actionlabel", "headertitle", "tablabel"]);
const COPY_PROPS = new Set(["placeholder", "alt", "description", "aria-description", "tooltip", "data-tooltip", "empty-text",
  "emptytext", "empty-label", "subtitle", "hint", "eyebrow", "caption", "message", "body", "text", "reason",
  "accessibilityhint", "sr-label"]);

// Keys in script objects whose string value is shown ({ label: "Delete", body: "…" }). Same split.
const LABEL_KEYS = ["label", "title", "confirmLabel", "cancelLabel", "actionLabel", "navLabel", "heading", "ariaLabel", "headerTitle", "tabBarLabel"];
const COPY_KEYS = ["body", "text", "description", "placeholder", "message", "hint", "emptyText", "tooltip", "subtitle", "reason", "summary", "detail", "note", "caption", "chip"];
const KEY_RE = new RegExp(
  String.raw`(?<![\w$.])(${[...LABEL_KEYS, ...COPY_KEYS].join("|")})\s*:\s*(?:\(\)\s*=>\s*)?(?:'((?:\\.|[^'\\\n])*)'|"((?:\\.|[^"\\\n])*)"|` +
    "`((?:\\\\.|[^`\\\\])*)`)",
  "g",
);
// Calls whose string arguments are shown: toasts, notices, thrown errors.
const CALL_RE = /(?<![\w$])(toast(?:\.\w+)?|showToast|showNotice|notices\.(?:post|update)|new (?:\w*Error)|notify|confirmAction)\s*\(/g;
// Assignments to an error or message holder (`error.value = "Couldn't save"`).
const ASSIGN_RE = /(?<![\w$.])(?:error|errorMessage|errorText|message|failure|notice|statusText|hint)\w*(?:\.value)?\s*=\s*(?:'((?:\\.|[^'\\\n])*)'|"((?:\\.|[^"\\\n])*)"|`((?:\\.|[^`\\])*)`)/g;
const STRING_RE = /'((?:\\.|[^'\\\n])*)'|"((?:\\.|[^"\\\n])*)"|`((?:\\.|[^`\\])*)`/g;

function lineOf(text: string, index: number, firstLine = 1): number {
  let line = firstLine;
  for (let i = 0; i < index; i++) if (text.charCodeAt(i) === 10) line++;
  return line;
}

/** Blank a span, keeping its line breaks so line numbers stay right. */
const blank = (s: string) => s.replace(/[^\n]/g, " ");

function clean(s: string): string {
  return s
    .replace(/\$\{(?:[^{}]|\{[^{}]*\})*\}/g, "{x}")
    .replace(/&nbsp;/g, " ")
    .replace(/&(?:apos|#39);/g, "'")
    .replace(/&amp;/g, "&")
    .replace(/&hellip;/g, "…")
    .replace(/&[a-z]+;/g, " ")
    .replace(/\s+/g, " ")
    .trim();
}

/** Worth reading: has words, and isn't a path, class list, key or URL. A string with "..." always is (`+ "..."`). */
export function worthy(s: string): boolean {
  if (/\.\.\.(?![\w.$([{])/.test(s)) return true;
  if ((s.match(/[A-Za-z]/g) ?? []).length < 2) return false;
  if (/=>|^[\w.-]+\/[\w./-]+$|^[#.][\w-]+$|^\w+(\.\w+)+$|^[a-z0-9]+(-[a-z0-9]+)+$|^\w+:[\w-]+$|^https?:|^[a-z]+[A-Z]\w*$|^[a-z_]+$/.test(s)) return false;
  // A Tailwind class list: several hyphenated or colon-prefixed tokens.
  if (/^(?:[!\w:/[\].%-]+\s+)+[!\w:/[\].%-]+$/.test(s) && /(^|\s)[a-z]+(-|:)[\w-]/.test(s) && !/[A-Z]/.test(s[0])) return false;
  return true;
}

function literals(expr: string): string[] {
  const out: string[] = [];
  for (const m of expr.matchAll(STRING_RE)) out.push(m[1] ?? m[2] ?? m[3] ?? "");
  return out;
}

// One tag: name, then attributes whose values may be quoted or (JSX) braced, so `onPress={() => go()}` doesn't end it.
const TAG_RE = /<(\/?)([A-Za-z][\w.:-]*)((?:\s+(?:"[^"]*"|'[^']*'|\{(?:[^{}]|\{(?:[^{}]|\{[^{}]*\})*\})*\}|[^>"'{])*)?)(\/?)>/g;
const ATTR_RE = /([:@#]?[\w.:-]+)(?:\s*=\s*(?:"([^"]*)"|'([^']*)'|\{((?:[^{}]|\{(?:[^{}]|\{[^{}]*\})*\})*)\}))?/g;

/** Text and string props in a template (a Vue <template> block, or a whole .tsx file). */
export function templatePhrases(file: string, text: string, firstLine: number, jsx: boolean): Phrase[] {
  const out: Phrase[] = [];
  const src = text.replace(/<!--[\s\S]*?-->/g, blank).replace(/\{\/\*[\s\S]*?\*\/\}/g, blank);
  const stack: string[] = [];
  const placeHere = (): Place => {
    for (let i = stack.length - 1; i >= 0; i--) {
      const tag = stack[i].toLowerCase().replace(/-/g, "");
      if (LABEL_TAGS.has(tag)) return "label";
      if (!INLINE_TAGS.has(tag)) return "copy";
    }
    return "copy";
  };
  const addText = (raw: string, at: number) => {
    if (!raw.trim() || (jsx && stack.length === 0)) return;
    const expr = jsx ? /\{((?:[^{}]|\{(?:[^{}]|\{[^{}]*\})*\})*)\}/g : /\{\{([\s\S]*?)\}\}/g;
    for (const m of raw.matchAll(expr)) {
      for (const s of literals(m[1])) {
        const t = clean(s);
        if (worthy(t)) out.push({ file, line: lineOf(src, at + m.index, firstLine), text: t, place: placeHere() });
      }
    }
    const t = clean(raw.replace(expr, " {x} "));
    if (worthy(t)) out.push({ file, line: lineOf(src, at + raw.length - raw.trimStart().length, firstLine), text: t, place: placeHere() });
  };
  let pos = 0;
  for (const m of src.matchAll(TAG_RE)) {
    const index = m.index;
    // In a .tsx file, `<` also starts generics (`useState<string>()`) and comparisons: only take tags after a
    // place JSX can start, or inside a JSX element.
    if (jsx && stack.length === 0 && !/(?:^|[(,=?:&|{}\[\]]|return|\n)\s*$/.test(src.slice(Math.max(0, index - 40), index))) continue;
    addText(src.slice(pos, index), pos);
    pos = index + m[0].length;
    const [whole, closing, tag, attrs] = m;
    // The attribute pattern can take the `/` of `/>`, so read self-closing off the tag's end.
    const selfClose = /\/\s*>$/.test(whole);
    if (closing) {
      const i = stack.lastIndexOf(tag);
      if (i >= 0) stack.length = i;
      continue;
    }
    const tagPlace: Place = LABEL_TAGS.has(tag.toLowerCase().replace(/-/g, "")) ? "label" : "copy";
    for (const a of (attrs ?? "").matchAll(ATTR_RE)) {
      const [attr, rawName, dq, sq, braced] = a;
      if (dq === undefined && sq === undefined && braced === undefined) continue;
      if (rawName.startsWith("@") || (rawName.startsWith("v-") && !rawName.startsWith("v-bind:"))) continue;
      const bound = rawName.startsWith(":") || rawName.startsWith("v-bind:") || braced !== undefined;
      const name = rawName.replace(/^v-bind:|^[:#]/, "").toLowerCase();
      const isLabel = LABEL_PROPS.has(name) || LABEL_PROPS.has(name.replace(/-/g, ""));
      if (!isLabel && !COPY_PROPS.has(name) && !COPY_PROPS.has(name.replace(/-/g, ""))) continue;
      const value = dq ?? sq ?? braced ?? "";
      const line = lineOf(src, index + whole.indexOf(attr), firstLine);
      for (const s of bound ? literals(value) : [value]) {
        const t = clean(s);
        if (worthy(t)) out.push({ file, line, text: t, place: isLabel || (tagPlace === "label" && name === "title") ? "label" : "copy" });
      }
    }
    if (!selfClose && !VOID_TAGS.has(tag.toLowerCase())) stack.push(tag);
  }
  addText(src.slice(pos), pos);
  return out;
}

/** Copy in script code: keyed strings, toasts and notices, thrown errors, error assignments. Comment lines skipped. */
export function scriptPhrases(file: string, text: string, firstLine: number): Phrase[] {
  const out: Phrase[] = [];
  const src = text
    .replace(/\/\*[\s\S]*?\*\//g, blank)
    .split("\n")
    .map((l) => (/^\s*\/\//.test(l) ? blank(l) : l.replace(/(^|[\s;,{}()])\/\/\s.*$/, (m, pre: string) => pre + blank(m.slice(pre.length)))))
    .join("\n");
  const seen = new Set<string>();
  const push = (index: number, raw: string, place: Place) => {
    const t = clean(raw);
    if (!worthy(t)) return;
    const line = lineOf(src, index, firstLine);
    const key = `${line}\u0000${t}`;
    if (seen.has(key)) return;
    seen.add(key);
    out.push({ file, line, text: t, place });
  };
  for (const m of src.matchAll(KEY_RE)) {
    push(m.index, m[2] ?? m[3] ?? m[4] ?? "", LABEL_KEYS.includes(m[1]) ? "label" : "copy");
  }
  for (const m of src.matchAll(CALL_RE)) {
    const rest = src.slice(m.index + m[0].length, m.index + m[0].length + 800);
    let depth = 1;
    let end = rest.length;
    for (let i = 0; i < rest.length; i++) {
      if (rest[i] === "(") depth++;
      else if (rest[i] === ")" && --depth === 0) {
        end = i;
        break;
      }
    }
    for (const s of literals(rest.slice(0, end))) if (/\s/.test(s.trim())) push(m.index, s, "copy");
  }
  // Any other string with "..." in it is words too, mostly a truncation (`text.slice(0, 77) + "..."`).
  for (const m of src.matchAll(STRING_RE)) {
    const s = m[1] ?? m[2] ?? m[3] ?? "";
    if (/\.\.\.(?![\w.$([{])/.test(s)) push(m.index, s, "copy");
  }
  for (const m of src.matchAll(ASSIGN_RE)) {
    const s = m[1] ?? m[2] ?? m[3] ?? "";
    if (/\s/.test(s.trim())) push(m.index, s, "copy");
  }
  return out;
}

/** A Vue file's top-level <template> and <script> blocks, nested <template> tags counted. */
function vueBlocks(content: string): { template: { text: string; firstLine: number }[]; script: { text: string; firstLine: number }[] } {
  const lines = content.split("\n");
  const template: { text: string; firstLine: number }[] = [];
  const script: { text: string; firstLine: number }[] = [];
  let open: { kind: "template" | "script"; start: number } | null = null;
  let depth = 0;
  lines.forEach((line, i) => {
    if (!open) {
      if (/^<template[\s>]/.test(line)) {
        open = { kind: "template", start: i + 1 };
        depth = 1;
      } else if (/^<script[\s>]/.test(line)) open = { kind: "script", start: i + 1 };
      return;
    }
    if (open.kind === "template") {
      depth += (line.match(/<template(?=[\s>]|$)/g) ?? []).length;
      depth -= (line.match(/<\/template>/g) ?? []).length;
      if (depth <= 0) {
        template.push({ text: lines.slice(open.start, i).join("\n"), firstLine: open.start + 1 });
        open = null;
      }
    } else if (/^<\/script>/.test(line)) {
      script.push({ text: lines.slice(open.start, i).join("\n"), firstLine: open.start + 1 });
      open = null;
    }
  });
  return { template, script };
}

/** Every phrase in one client or phone source file. */
export function phrasesOf(file: string, content: string): Phrase[] {
  if (file.endsWith(".vue")) {
    const { template, script } = vueBlocks(content);
    return [
      ...template.flatMap((b) => templatePhrases(file, b.text, b.firstLine, false)),
      ...script.flatMap((b) => scriptPhrases(file, b.text, b.firstLine)),
    ];
  }
  if (file.endsWith(".tsx")) return [...templatePhrases(file, content, 1, true), ...scriptPhrases(file, content, 1)];
  return scriptPhrases(file, content, 1);
}

// The server: a C# string literal that reads as a sentence (a capital, words, an ending stop) and isn't a log line.
// Log messages, analyzer text and agent-facing tool descriptions aren't shown in the app, so they're skipped.
const CS_STRING_RE = /\$?@?"((?:\\.|""|[^"\\\n])*)"/g;
export function serverPhrases(file: string, content: string): Phrase[] {
  const out: Phrase[] = [];
  content.split("\n").forEach((raw, i) => {
    if (/^\s*(\/\/|\*|\/\*|\[)/.test(raw)) return;
    if (/\b(Log(Trace|Debug|Information|Warning|Error|Critical)|LoggerMessage|Description\s*\(|Debug\.|Trace\.|Console\.)/.test(raw)) return;
    for (const m of raw.matchAll(CS_STRING_RE)) {
      const t = clean(m[1].replace(/\{[^{}]*\}/g, "{x}"));
      const sentence = /^[A-Z{]/.test(t) && /[a-z]{2,} [a-z]{2,}/.test(t) && /[.?!…]$/.test(t);
      const trailing = /^[A-Z][a-z]+(?: [\w'{}]+)*\.\.\.$/.test(t); // "Waiting..."
      if (!sentence && !trailing) continue;
      out.push({ file, line: i + 1, text: t, place: "copy", server: true });
    }
  });
  return out;
}

// ─── The rules ───────────────────────────────────────────────────────────────────────────────────────────────────────

// "Computer" for the desk as against the phone or the OS, and `fleet node`, the command; Node.js the runtime.
// Not part of a path, a file, a field or a service name (`node/page.html`, `x.nodes`, `fleet-node`).
const MACHINE_WORD = /(?<![\w/.\-])(computers?|nodes?|Fleets)(?![\w/\-]|\.\w)/gi;
const MACHINE_WORD_OK = [
  /\bfleet node\b/i, // the command: `fleet node up`
  /\bnode\.?js\b/i,
  /\bnode_modules\b/,
  /\bnode (?:up|pair|run)\b/i, // `fleet node up`, written short
  // The desk as against the phone, said on the phone. Exact phrases: "Enter the code from your computer" meant a
  // Fleet machine and was changed (#991), so a looser pattern would let that back in.
  /\bOpen on my computer\b/,
  /\bOpen it on the computer\b/,
  /\bSends a link to the computer\b/,
  /\bwhile Fleet is open on a computer\b/,
  /\bAdd machines from Fleet on a computer\b/,
  /\bOn my computer\b/, // the desk, as against a server: "Where will Fleet run?" on the phone
  /\bGet Fleet for your computer\b/, // the share sheet text, sent to the desk
];

const SMALL = new Set(["a", "an", "and", "or", "of", "the", "to", "in", "on", "for", "at", "by", "with", "vs", "as", "from",
  "is", "it", "up", "into", "your", "no", "not", "per", "via", "off", "out", "all"]);
// Names, not words: products, platforms, keys, and Fleet's own rail and Settings page names, which are capitalised
// wherever they're named ("Open Settings → Labs").
export const PROPER_NOUNS = new Set([
  "Fleet", "Weave", "GitHub", "OpenCode", "Claude", "Copilot", "Pi", "Codex", "Git", "Markdown", "Mermaid",
  "macOS", "Windows", "Linux", "iOS", "Android", "OAuth", "Tailscale", "MagicDNS", "Anthropic", "OpenAI", "Opus", "Sonnet",
  "Haiku", "Bun", "Node", "Chrome", "Chromium", "Edge", "Firefox", "Safari", "Notion", "Linear", "Slack", "Jira",
  "Expo", "Cursor", "Zed", "Finder", "Explorer", "Docker", "Playwright", "Vue", "React", "Kanban", "HydraFusion",
  "Esc", "Ctrl", "Shift", "Cmd", "Alt",
  "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun",
  "January", "February", "March", "April", "June", "July", "August", "September", "October", "November", "December",
  // The rail and its views, and the Context canvas ("Show in Context")
  "Sessions", "Settings", "Analytics", "Automations", "Jobs", "Workflows", "Board", "Labs", "Repositories", "Context",
  // Settings pages whose names aren't everyday words ("Turn it on in Memory")
  "Memory", "Dreaming", "Machines", "Mods", "Skills", "Harnesses", "Integrations", "Plugins",
]);
// Names of more than one word, taken out before the words are read ("Open in VS Code").
const PROPER_PHRASES = /\b(?:Visual Studio Code|VS Code|Claude Code|GitHub Copilot|GitHub Actions|Weave Fleet|OpenCode \d|Google Chrome|Microsoft Edge|Stage \d+|Pull Requests?(?= on GitHub))\b/g;

/** Title Case: two or more words, the first capitalised and every other real word too ("New Project", "Add Lane"). */
export function isTitleCase(text: string): boolean {
  // Words inside quotes, code or after an arrow name something ("Open Settings → Labs"): drop them.
  // So do a shortcut in brackets ("Clear (Esc)") and names of more than one word.
  const plain = text
    .replace(/“[^”]*”|"[^"]*"|`[^`]*`|‘[^’]*’|\{x\}|\([^)]*\)/g, " ")
    .replace(PROPER_PHRASES, " ")
    .replace(/[→›»:].*$/, "");
  const words = plain.match(/[A-Za-z][A-Za-z'’]*/g) ?? [];
  if (words.length < 2) return false;
  const [first, ...rest] = words;
  if (!first || !/^[A-Z]/.test(first)) return false;
  const significant = rest.filter((w) => !SMALL.has(w.toLowerCase()) && !PROPER_NOUNS.has(w.replace(/['’]s$/, "")) && !/^[A-Z0-9]+s?$/.test(w) && w.length > 1);
  return significant.length > 0 && significant.every((w) => /^[A-Z]/.test(w));
}

// Labels that are names, so their capitals are right: themes and code themes, and a few product phrases.
export const TITLE_CASE_OK = new Set([
  "Default Dark", "Default Light", "Weave Classic", "Solarized Dark", "Solarized Light", "GitHub Dark", "GitHub Light",
  "One Dark", "One Light", "Tokyo Night", "Catppuccin Mocha", "Catppuccin Latte", "Rosé Pine", "Rose Pine", "Gruvbox Dark",
  "Nord", "Dracula", "Monokai", "Night Owl", "Ayu Dark", "Ayu Light", "Vitesse Dark", "Vitesse Light", "Material Theme",
  "Min Dark", "Min Light", "Slack Dark", "Slack Ochin", "Everforest Dark", "Everforest Light", "Kanagawa Wave",
  // Tool names, as the harness names them (WebFetch, WebSearch, NotebookEdit)
  "Web Fetch", "Web Search", "Notebook Edit",
]);

export function checkPhrase(p: Phrase): Violation[] {
  const out: Violation[] = [];
  const at = (rule: Rule, detail: string) => out.push({ rule, file: p.file, line: p.line, detail });
  const shown = p.text.length > 90 ? p.text.slice(0, 87) + "…" : p.text;

  for (const m of p.text.matchAll(MACHINE_WORD)) {
    const around = p.text.slice(Math.max(0, m.index - 60), m.index + m[0].length + 60);
    if (MACHINE_WORD_OK.some((ok) => ok.test(around))) continue;
    // "Fleets" only counts capitalised; "fleets" in prose is rare and usually the word.
    if (m[0].toLowerCase() === "fleets" && m[0] !== "Fleets") continue;
    at("MACHINE_WORD", `"${m[0]}" in "${shown}"; say "machine"`);
    break;
  }
  if (/\.\.\.(?![\w.$([{])/.test(p.text)) {
    at("THREE_DOTS", `"..." in "${shown}"; use "…"`);
  }
  if (p.server) return out;
  if (/^retry[.…!]*$/i.test(p.text)) at("RETRY_LABEL", `"${p.text}"; say "Try again"`);
  if (p.place === "label" && !TITLE_CASE_OK.has(p.text) && isTitleCase(p.text)) {
    at("TITLE_CASE", `"${shown}"; write labels in sentence case`);
  }
  return out;
}

// ─── Files, counts and the ratchet ──────────────────────────────────────────────────────────────────────────────────

// Run from client/ (npm and bun scripts do).
const clientRoot = process.cwd();

const SKIP_DIRS = new Set(["__tests__", "node_modules", "mocks", "generated", "bin", "obj", "Migrations", "wwwroot"]);

function findFiles(dir: string, extensions: string[]): string[] {
  if (!existsSync(dir)) return [];
  const files: string[] = [];
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    const full = join(dir, entry.name);
    if (entry.isDirectory()) {
      if (!SKIP_DIRS.has(entry.name) && !/Tests?$/.test(entry.name)) files.push(...findFiles(full, extensions));
    } else if (
      extensions.some((ext) => entry.name.endsWith(ext)) &&
      !/\.(test|spec|d|gen)\.tsx?$|Tests?\.cs$|^test-setup\.ts$/.test(entry.name)
    ) {
      files.push(full);
    }
  }
  return files.sort();
}

/** Every violation in the client, the phone app and the server, with paths relative to the repository root. */
export function lintWords(root: string = join(clientRoot, "..")): Violation[] {
  const rel = (full: string) => relative(root, full).split(sep).join("/");
  const read = (full: string) => readFileSync(full, "utf8");
  const client = findFiles(join(root, "client", "src"), [".vue", ".ts", ".tsx"]).flatMap((f) => phrasesOf(rel(f), read(f)));
  const phone = findFiles(join(root, "mobile", "src"), [".ts", ".tsx"]).flatMap((f) => phrasesOf(rel(f), read(f)));
  const server = findFiles(join(root, "src"), [".cs"]).flatMap((f) => serverPhrases(rel(f), read(f)));
  return [...client, ...phone, ...server].flatMap(checkPhrase);
}

function main(argv: string[]): number {
  const violations = lintWords();
  const listAt = argv.indexOf("--list");
  const rule = listAt >= 0 ? argv[listAt + 1] : undefined;
  const shown = violations.filter((v) => !rule || v.rule === rule);
  for (const v of shown) console.log(`[${v.rule}] ${v.file}:${v.line}: ${v.detail}`);
  if (listAt >= 0) return 0;
  if (violations.length === 0) {
    console.log(`Words lint: ✓ ${RULES.join(", ")}`);
    return 0;
  }
  console.log(
    `\nWords lint: ${violations.length} to fix. See the words rules in AGENTS.md's Design Rules. A use that's right (a name, ` +
      "the desk as against the phone) goes on the allow-lists in scripts/lint-words.ts, with its reason.",
  );
  return 1;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  process.exit(main(process.argv.slice(2)));
}
