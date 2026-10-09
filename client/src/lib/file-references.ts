/**
 * Files named in a reply: inline code that is a path (`src/billing/tax.ts:13`) and Markdown links to a file
 * (`[tax.ts](src/billing/tax.ts#L18)`) open the file in a tab, at the line. The renderer marks what looks like a path;
 * the server says which of those are files in the session's folder, and only those become links.
 */
import type MarkdownIt from "markdown-it";
import type Token from "markdown-it/lib/token.mjs";

export interface FileReference {
  /** The path as written, without the line. */
  path: string;
  line?: number;
}

/**
 * What the conversation passes to `render` as its env. `fileRefs` collects every path the reply names; `resolveFileRef`
 * says what each one is: the path within the session's folder, null when it isn't a file there, undefined when not
 * known yet.
 */
export interface FileReferenceEnv {
  fileRefs?: string[];
  resolveFileRef?: (path: string) => string | null | undefined;
}

// `:42`, `:42:7`, `:42-50`, `#L42`, `#L42-L50`
const LINE_SUFFIX = /(?::(\d+)(?::\d+)?(?:-\d+)?|#L(\d+)(?:-L?\d+)?)$/;
// An optional drive, home or `./` start, then names joined by `/` or `\`. No spaces, quotes or brackets.
const PATH_SHAPE = /^(?:[A-Za-z]:[\\/]|~[\\/]|\.{1,2}[\\/]|[\\/])?(?:[\w.@+-]+[\\/])*[\w.@+-]+$/;
// A file name with an extension (`tax.ts`, `.gitignore`), not `1.2.3` or `this.total`.
const FILE_NAME = /(?:^|[\\/])[\w.@+-]*\.[a-z0-9]*[a-z][a-z0-9]*$/;
const MAX_PATH_LENGTH = 260;

/** The path and line in `text`, when it looks like a file: a name with an extension, or a path with a folder. */
export function parseFileReference(text: string): FileReference | null {
  const trimmed = text.trim();
  if (!trimmed || trimmed.length > MAX_PATH_LENGTH || trimmed.includes("://")) return null;

  const suffix = LINE_SUFFIX.exec(trimmed);
  const path = suffix ? trimmed.slice(0, suffix.index) : trimmed;
  if (!PATH_SHAPE.test(path) || /^\.+$/.test(path)) return null;
  if (!FILE_NAME.test(path) && !/[\\/]/.test(path.replace(/^[A-Za-z]:|[\\/]+$/g, ""))) return null;

  const line = suffix ? Number(suffix[1] ?? suffix[2]) : undefined;
  return line ? { path, line } : { path };
}

/** A Markdown link's target as a file reference, when it isn't a web address, an anchor or a Fleet page. */
function linkReference(href: string): FileReference | null {
  if (!href || href.startsWith("#") || href.startsWith("//") || /^[a-z][a-z0-9+.-]+:/i.test(href)) return null;
  let decoded = href;
  try {
    decoded = decodeURI(href);
  } catch {
    return null;
  }
  return parseFileReference(decoded);
}

const isMac = typeof navigator !== "undefined" && /Mac|iPhone|iPad/.test(navigator.platform);
const KEEP_HINT = `${isMac ? "⌘" : "Ctrl"}-click keeps the tab`;

function mark(token: Token, reference: FileReference, env: FileReferenceEnv): void {
  env.fileRefs?.push(reference.path);
  const path = env.resolveFileRef?.(reference.path);
  if (!path) return;

  token.attrJoin("class", "file-ref");
  token.attrSet("data-file-path", path);
  if (reference.line) token.attrSet("data-file-line", String(reference.line));
  token.attrSet("title", `Open ${path}${reference.line ? ` at line ${reference.line}` : ""} · ${KEEP_HINT}`);
  if (token.type === "code_inline") {
    token.attrSet("role", "link");
    token.attrSet("tabindex", "0");
  }
}

/** Marks inline code and links that name a file in the session's folder; see {@link FileReferenceEnv}. */
export function fileReferences(md: MarkdownIt): void {
  md.core.ruler.push("file-references", (state) => {
    const env = state.env as FileReferenceEnv;
    if (!env.fileRefs && !env.resolveFileRef) return;

    for (const block of state.tokens) {
      for (const token of block.children ?? []) {
        if (token.type === "code_inline") {
          const reference = parseFileReference(token.content);
          if (reference) mark(token, reference, env);
        } else if (token.type === "link_open" && token.markup !== "linkify") {
          const reference = linkReference(token.attrGet("href") ?? "");
          if (reference) mark(token, reference, env);
        }
      }
    }
  });
}

/**
 * A file name in prose (`README.md`, `main.rs`) isn't a web address. Linkify reads it as one because `.md` and `.rs`
 * are country domains; a bare name, without `www.`, a scheme or a path, is left as text.
 */
export function bareFileNamesAreText(md: MarkdownIt): void {
  md.core.ruler.after("linkify", "bare-file-names", (state) => {
    for (const block of state.tokens) {
      const children = block.children;
      if (!children) continue;
      for (let i = children.length - 3; i >= 0; i--) {
        const [open, text, close] = [children[i], children[i + 1], children[i + 2]];
        if (open.type !== "link_open" || open.markup !== "linkify" || text.type !== "text" || close.type !== "link_close") continue;
        if (!/^[\w-]+(?:\.[\w-]+)*\.[a-z]{2}$/i.test(text.content) || /^www\./i.test(text.content)) continue;
        children.splice(i, 3, text);
      }
    }
  });
}
