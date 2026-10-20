import MarkdownIt from "markdown-it";
import type StateBlock from "markdown-it/lib/rules_block/state_block.mjs";
import hljs from "highlight.js/lib/core";
import bash from "highlight.js/lib/languages/bash";
import csharp from "highlight.js/lib/languages/csharp";
import css from "highlight.js/lib/languages/css";
import diff from "highlight.js/lib/languages/diff";
import go from "highlight.js/lib/languages/go";
import javascript from "highlight.js/lib/languages/javascript";
import json from "highlight.js/lib/languages/json";
import markdown from "highlight.js/lib/languages/markdown";
import plaintext from "highlight.js/lib/languages/plaintext";
import python from "highlight.js/lib/languages/python";
import rust from "highlight.js/lib/languages/rust";
import shell from "highlight.js/lib/languages/shell";
import typescript from "highlight.js/lib/languages/typescript";
import xml from "highlight.js/lib/languages/xml";
import yaml from "highlight.js/lib/languages/yaml";
import c from "highlight.js/lib/languages/c";
import cpp from "highlight.js/lib/languages/cpp";
import dockerfile from "highlight.js/lib/languages/dockerfile";
import ini from "highlight.js/lib/languages/ini";
import java from "highlight.js/lib/languages/java";
import kotlin from "highlight.js/lib/languages/kotlin";
import php from "highlight.js/lib/languages/php";
import powershell from "highlight.js/lib/languages/powershell";
import ruby from "highlight.js/lib/languages/ruby";
import sql from "highlight.js/lib/languages/sql";
import swift from "highlight.js/lib/languages/swift";
import { bareFileNamesAreText, fileReferences } from "@/lib/file-references";

hljs.registerLanguage("bash", bash);
hljs.registerLanguage("csharp", csharp);
hljs.registerLanguage("cs", csharp);
hljs.registerLanguage("css", css);
hljs.registerLanguage("diff", diff);
hljs.registerLanguage("go", go);
hljs.registerLanguage("javascript", javascript);
hljs.registerLanguage("js", javascript);
hljs.registerLanguage("json", json);
hljs.registerLanguage("markdown", markdown);
hljs.registerLanguage("md", markdown);
hljs.registerLanguage("plaintext", plaintext);
hljs.registerLanguage("text", plaintext);
hljs.registerLanguage("python", python);
hljs.registerLanguage("py", python);
hljs.registerLanguage("rust", rust);
hljs.registerLanguage("rs", rust);
hljs.registerLanguage("shell", shell);
hljs.registerLanguage("sh", shell);
hljs.registerLanguage("typescript", typescript);
hljs.registerLanguage("ts", typescript);
hljs.registerLanguage("tsx", typescript);
hljs.registerLanguage("html", xml);
hljs.registerLanguage("xml", xml);
hljs.registerLanguage("yaml", yaml);
hljs.registerLanguage("yml", yaml);

// Each language brings its own short names (c++, kt, rb, ps1, docker, toml, h, hpp, kts…); registering adds them.
hljs.registerLanguage("java", java);
hljs.registerLanguage("c", c);
hljs.registerLanguage("cpp", cpp);
hljs.registerLanguage("ruby", ruby);
hljs.registerLanguage("php", php);
hljs.registerLanguage("sql", sql);
hljs.registerLanguage("kotlin", kotlin);
hljs.registerLanguage("swift", swift);
hljs.registerLanguage("dockerfile", dockerfile);
hljs.registerLanguage("powershell", powershell);
hljs.registerLanguage("ini", ini);

function escapeHtml(value: string): string {
  return value
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#039;");
}

let sharedRenderer: MarkdownIt | null = null;

/**
 * One renderer for every message in a conversation. Building one compiles its link patterns, which cost more than
 * rendering most messages; a switch into a long session built one per message and per reasoning block.
 */
export function sharedMarkdownRenderer(): MarkdownIt {
  sharedRenderer ??= createMarkdownRenderer();
  return sharedRenderer;
}

// "- [ ] todo" and "- [x] done", as GitHub draws them.
const TASK_MARKER = /^\[([ xX])\]\s+/;

/**
 * Marks task list items with a class (the checkbox is drawn in CSS) and drops the `[ ]` / `[x]` text, so pull
 * request test plans read as checklists. No HTML is added, so the sanitizer has nothing new to allow.
 */
function taskLists(md: MarkdownIt): void {
  md.core.ruler.after("inline", "task-lists", (state) => {
    const tokens = state.tokens;
    for (let i = 2; i < tokens.length; i++) {
      const inline = tokens[i];
      if (inline.type !== "inline" || tokens[i - 1].type !== "paragraph_open" || tokens[i - 2].type !== "list_item_open") continue;
      const match = TASK_MARKER.exec(inline.content);
      const first = inline.children?.[0];
      if (!match || first?.type !== "text" || !TASK_MARKER.test(first.content)) continue;

      first.content = first.content.replace(TASK_MARKER, "");
      inline.content = inline.content.replace(TASK_MARKER, "");
      tokens[i - 2].attrJoin("class", match[1] === " " ? "task-list-item" : "task-list-item task-list-item--done");
    }
  });
}

/**
 * What a caller passes to `render` as its env to control folds. `streaming` is true while the message is still being
 * written: an unclosed fold draws open with its text so far, and a closed one stays open until the message settles.
 * `folds` holds what the reader chose, by position (the nth fold in the message): a redraw keeps it.
 */
export interface FoldEnv {
  streaming?: boolean;
  folds?: Map<string, boolean>;
  foldCount?: number;
}

const DETAILS_OPEN = /^<details( open)?>$/;
const SUMMARY_LINE = /^<summary>(.*)<\/summary>$/;
const TAG_STARTS = ["<details>", "<details open>", "</details>"];

/** A last line that is only the start of a fold tag (`<deta`, `<summary>Why the m`) isn't text yet. */
function isPartialTag(line: string): boolean {
  if (line.length === 0 || !line.startsWith("<")) return false;
  if (TAG_STARTS.some((tag) => tag !== line && tag.startsWith(line))) return true;
  if ("<summary>".startsWith(line) && line !== "<summary>") return true;
  return line.startsWith("<summary>") && !line.endsWith("</summary>");
}

/** The text of line `n`, without its indent. */
function lineText(state: StateBlock, n: number): string {
  return state.src.slice(state.bMarks[n] + state.tShift[n], state.eMarks[n]).trimEnd();
}

/** The line holding the `</details>` that closes the fold opened at `start`; -1 when it hasn't arrived. Fences are skipped. */
function findFoldEnd(state: StateBlock, start: number, last: number): number {
  let depth = 1;
  let fence: { char: string; length: number } | null = null;
  for (let n = start + 1; n < last; n++) {
    if (state.sCount[n] - state.blkIndent >= 4) continue;
    const text = lineText(state, n);
    const mark = /^(`{3,}|~{3,})(.*)$/.exec(text);
    if (fence) {
      if (mark && mark[1][0] === fence.char && mark[1].length >= fence.length && mark[2].trim() === "") fence = null;
      continue;
    }
    if (mark) {
      fence = { char: mark[1][0], length: mark[1].length };
      continue;
    }
    if (DETAILS_OPEN.test(text)) depth++;
    else if (text === "</details>" && --depth === 0) return n;
  }
  return -1;
}

/**
 * `<details>` / `<details open>`, an optional `<summary>one line</summary>` and `</details>`, each on its own line, become a
 * fold. HTML stays off: only these exact lines are recognised and the tokens are fixed, so any other attribute or
 * tag is left as escaped text.
 */
function folds(md: MarkdownIt): void {
  md.core.ruler.before("block", "fold-partial-tag", (state) => {
    if (!(state.env as FoldEnv | undefined)?.streaming) return;
    const cut = state.src.lastIndexOf("\n") + 1;
    if (isPartialTag(state.src.slice(cut).trim())) state.src = state.src.slice(0, cut);
  });

  md.block.ruler.before("fence", "details", (state, startLine, endLine, silent) => {
    if (state.sCount[startLine] - state.blkIndent >= 4) return false;
    const opening = DETAILS_OPEN.exec(lineText(state, startLine));
    if (!opening) return false;

    const env = state.env as FoldEnv;
    const end = findFoldEnd(state, startLine, endLine);
    const closed = end !== -1;
    if (!closed && !env.streaming) return false;
    if (silent) return true;

    const summaryLine = startLine + 1 < endLine ? lineText(state, startLine + 1) : null;
    const summary = summaryLine === null ? null : SUMMARY_LINE.exec(summaryLine);
    // The fold waits until its summary line has fully arrived.
    if (!closed && (summaryLine === null || (!summary && isPartialTag(summaryLine)))) {
      state.line = endLine;
      return true;
    }

    const index = env.foldCount ?? 0;
    env.foldCount = index + 1;
    const key = String(index);
    const open = env.folds?.get(key) ?? (env.streaming ? true : opening[1] !== undefined);

    const details = state.push("details_open", "details", 1);
    details.map = [startLine, closed ? end + 1 : endLine];
    details.attrSet("class", "md-details");
    details.attrSet("data-fold", key);
    if (open) details.attrSet("open", "");
    const summaryOpen = state.push("summary_open", "summary", 1);
    summaryOpen.block = false;
    const inline = state.push("inline", "", 0);
    inline.content = summary ? summary[1] : "Details";
    inline.children = [];
    const summaryClose = state.push("summary_close", "summary", -1);
    summaryClose.block = false;
    state.push("details_body_open", "div", 1).attrSet("class", "md-details__body");

    const bodyStart = startLine + (summary ? 2 : 1);
    const bodyEnd = closed ? end : endLine;
    const parentType = state.parentType;
    const lineMax = state.lineMax;
    state.parentType = "details" as typeof state.parentType;
    state.lineMax = bodyEnd;
    if (bodyStart < bodyEnd) state.md.block.tokenize(state, bodyStart, bodyEnd);
    state.parentType = parentType;
    state.lineMax = lineMax;

    state.push("details_body_close", "div", -1);
    state.push("details_close", "details", -1);
    state.line = closed ? end + 1 : endLine;
    return true;
  }, { alt: ["paragraph", "reference", "blockquote", "list"] });
}

/**
 * Remembers a click on a fold's summary in `folds`, so the next draw keeps it as the reader left it. The click runs
 * before the browser flips `open`, so the new state is the opposite of what the element has now.
 */
export function rememberFoldClick(event: MouseEvent, folds: Map<string, boolean>): void {
  const summary = (event.target as Element | null)?.closest?.("summary");
  const details = summary?.parentElement;
  const key = details?.getAttribute("data-fold");
  if (details instanceof HTMLDetailsElement && details.matches(".md-details") && key) folds.set(key, !details.open);
}

export function createMarkdownRenderer(): MarkdownIt {
  return new MarkdownIt({
    html: false,
    linkify: true,
    breaks: true,
    highlight(code, language) {
      // Left as source; a renderer that draws diagrams swaps these for SVG after mounting.
      if (language.toLowerCase() === "mermaid") {
        return `<pre class="hljs mermaid-source"><code>${escapeHtml(code)}</code></pre>`;
      }

      if (language && hljs.getLanguage(language)) {
        return `<pre class="hljs"><code>${hljs.highlight(code, {
          language,
          ignoreIllegals: true,
        }).value}</code></pre>`;
      }

      return `<pre class="hljs"><code>${escapeHtml(code)}</code></pre>`;
    },
  }).use(folds).use(taskLists).use(bareFileNamesAreText).use(fileReferences);
}
