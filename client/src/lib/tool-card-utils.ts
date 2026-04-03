/**
 * Shared utilities for specialized tool card components.
 *
 * Used by tool-cards/ to extract language, count lines, parse output, etc.
 */

/** Map common file extensions to highlight.js language identifiers. */
const EXTENSION_MAP: Record<string, string> = {
  ts: "typescript",
  tsx: "typescript",
  js: "javascript",
  jsx: "javascript",
  mjs: "javascript",
  cjs: "javascript",
  py: "python",
  rb: "ruby",
  rs: "rust",
  go: "go",
  java: "java",
  kt: "kotlin",
  kts: "kotlin",
  cs: "csharp",
  fs: "fsharp",
  css: "css",
  scss: "scss",
  less: "less",
  html: "html",
  htm: "html",
  xml: "xml",
  svg: "xml",
  json: "json",
  yaml: "yaml",
  yml: "yaml",
  toml: "ini",
  md: "markdown",
  mdx: "markdown",
  sh: "bash",
  bash: "bash",
  zsh: "bash",
  fish: "bash",
  ps1: "powershell",
  sql: "sql",
  graphql: "graphql",
  gql: "graphql",
  dockerfile: "dockerfile",
  docker: "dockerfile",
  makefile: "makefile",
  cmake: "cmake",
  c: "c",
  cpp: "cpp",
  cc: "cpp",
  h: "c",
  hpp: "cpp",
  swift: "swift",
  php: "php",
  lua: "lua",
  r: "r",
  scala: "scala",
  zig: "zig",
  tf: "hcl",
  hcl: "hcl",
  env: "bash",
  ini: "ini",
  cfg: "ini",
  conf: "ini",
};

/** Well-known filenames that imply a language. */
const FILENAME_MAP: Record<string, string> = {
  dockerfile: "dockerfile",
  makefile: "makefile",
  cmakelists: "cmake",
  gemfile: "ruby",
  rakefile: "ruby",
  justfile: "makefile",
};

/**
 * Detect the highlight.js language identifier from a file path.
 * Returns an empty string if the language cannot be determined.
 */
export function getLanguageFromPath(filePath: string): string {
  const segments = filePath.split("/");
  const filename = segments[segments.length - 1]?.toLowerCase() ?? "";

  // Check well-known filenames first
  const baseName = filename.split(".")[0];
  if (baseName && FILENAME_MAP[baseName]) {
    return FILENAME_MAP[baseName];
  }

  // Check extension
  const ext = filename.includes(".") ? filename.split(".").pop()?.toLowerCase() ?? "" : "";
  return EXTENSION_MAP[ext] ?? "";
}

/** Count the number of lines in a text string. Returns 0 for empty strings. */
export function countLines(text: string): number {
  if (!text) return 0;
  // Count newlines; a trailing newline doesn't add an extra "line"
  const lines = text.split("\n");
  // If the last element is empty (trailing newline), exclude it
  if (lines[lines.length - 1] === "") {
    return lines.length - 1;
  }
  return lines.length;
}

/** Compute a unified diff summary: +added / -removed line counts. */
export function diffSummary(oldStr: string, newStr: string): { added: number; removed: number } {
  const oldLines = oldStr.split("\n");
  const newLines = newStr.split("\n");
  // Simple heuristic: lines unique to old are removed, unique to new are added
  // For accurate line-level diff, use a proper diff algorithm later
  return {
    removed: oldLines.length,
    added: newLines.length,
  };
}

/**
 * Parse grep output into a list of file:line entries.
 * Expected format: one `file:line` per line.
 */
export function parseGrepOutput(output: string): { file: string; line: number }[] {
  if (!output) return [];
  const results: { file: string; line: number }[] = [];
  for (const raw of output.split("\n")) {
    const trimmed = raw.trim();
    if (!trimmed) continue;
    // Format: "file/path.ts:123" or "file/path.ts:  Line 123: content"
    const match = trimmed.match(/^(.+?):(\d+)/);
    if (match) {
      results.push({ file: match[1], line: parseInt(match[2], 10) });
    }
  }
  return results;
}

/**
 * Parse glob output into a list of file paths.
 * Expected format: one path per line.
 */
export function parseGlobOutput(output: string): string[] {
  if (!output) return [];
  return output
    .split("\n")
    .map((l) => l.trim())
    .filter(Boolean);
}
