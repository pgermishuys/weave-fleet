import MarkdownIt from "markdown-it";
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
  }).use(taskLists);
}
