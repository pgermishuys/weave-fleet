import { completeAnyWord, type Completion, type CompletionContext, type CompletionResult, type CompletionSource } from "@codemirror/autocomplete";
import { EditorState, type Extension } from "@codemirror/state";

/**
 * Autocomplete, tiers 1 and 2: words already in the file, the language's own keywords, snippets and
 * locals (they come with the language), and import paths from the session's files. No language
 * server and no AI.
 */

/** Lists a folder of the session's directory: repo-relative paths, folders ending in "/". */
export type ListFolder = (folder: string) => Promise<readonly string[]>;

// `from "…`, `import "…`, `import("…`, `require("…`, `export … from "…` up to the cursor.
const IMPORT_BEFORE = /(?:\bfrom\s*|\bimport\s*\(?\s*|\brequire\s*\(\s*)(["'])([^"'\n]*)$/;
// Extensions a JS/TS import usually leaves out.
const IMPLIED_EXTENSION = /(?:\.d)?\.[cm]?[jt]sx?$/;

function directoryOf(path: string): string {
  const slash = path.lastIndexOf("/");
  return slash < 0 ? "" : path.slice(0, slash + 1);
}

/** Resolve "./", "../" and "a/b" against a folder, without leaving the repo. */
function joinFolder(base: string, relative: string): string | null {
  const parts = base.split("/").filter(Boolean);
  for (const part of relative.split("/").filter(Boolean)) {
    if (part === ".") continue;
    if (part === "..") {
      if (parts.length === 0) return null;
      parts.pop();
    } else {
      parts.push(part);
    }
  }
  return parts.length === 0 ? "" : `${parts.join("/")}/`;
}

/** The `src/` folder that `@/` points at: the nearest one above the file, else the repo's. */
function aliasRoot(currentPath: string): string {
  const index = currentPath.lastIndexOf("/src/");
  return index >= 0 ? currentPath.slice(0, index + "/src/".length) : "src/";
}

/** The repo folder an import specifier's folder part points at, or null for a package import. */
export function importFolder(currentPath: string, typedFolder: string): string | null {
  if (typedFolder.startsWith("./") || typedFolder.startsWith("../")) {
    return joinFolder(directoryOf(currentPath), typedFolder);
  }
  if (typedFolder.startsWith("@/")) {
    return joinFolder(aliasRoot(currentPath), typedFolder.slice(2));
  }
  return null;
}

export function importPathSource(currentPath: string, listFolder: ListFolder): CompletionSource {
  return async (context: CompletionContext): Promise<CompletionResult | null> => {
    const line = context.state.doc.lineAt(context.pos);
    const before = line.text.slice(0, context.pos - line.from);
    const match = IMPORT_BEFORE.exec(before);
    if (!match) return null;

    // Paths complete one folder at a time, from the first "/": "./", "../", "@/".
    const typed = match[2];
    const typedFolder = directoryOf(typed);
    if (!typedFolder) return null;

    const folder = importFolder(currentPath, typedFolder);
    if (folder === null) return null;

    let entries: readonly string[];
    try {
      entries = await listFolder(folder);
    } catch {
      return null;
    }
    if (context.aborted) return null;

    const options: Completion[] = [];
    for (const entry of entries) {
      const isFolder = entry.endsWith("/");
      const name = entry.slice(folder.length);
      if (!name || entry === currentPath) continue;
      options.push({
        label: isFolder ? name : name.replace(IMPLIED_EXTENSION, ""),
        detail: isFolder ? "folder" : name,
        type: isFolder ? "namespace" : "file",
        boost: isFolder ? 1 : 0,
      });
    }

    return {
      // Replace only the segment after the last "/".
      from: context.pos - (typed.length - typedFolder.length),
      options,
      validFor: /^[\w@.-]*$/,
    };
  };
}

/** Word and import completions for any language; the language adds its own on top. */
export function completionSources(currentPath: string, listFolder: ListFolder | null): Extension {
  const sources: CompletionSource[] = [completeAnyWord];
  if (listFolder) sources.unshift(importPathSource(currentPath, listFolder));
  return EditorState.languageData.of(() => sources.map((autocomplete) => ({ autocomplete })));
}
