import type { Register } from "fleet-mods";

// Draws grep and glob results as "14 matches in 5 files", and the files with the most matches when the row is opened.
export type Hits = { matches: number; files: { file: string; count: number }[]; listing: boolean };

/** Reads a search tool's output: OpenCode's grouped list, `path:line:text` lines, or a plain list of paths. */
export function parse(raw: string, tool: string): Hits | undefined {
  const lines = raw.split(/\r?\n/).filter((l) => l.trim() !== "");
  if (lines.length === 0 || /^No (files|matches) found/i.test(lines[0])) return { matches: 0, files: [], listing: tool !== "grep" };
  const counts = new Map<string, number>();
  const hit = (file: string) => counts.set(file, (counts.get(file) ?? 0) + 1);

  // OpenCode grep: "Found 14 matches", then "/path/file.ts:" with "  Line 12: text" under it.
  let current = "";
  for (const line of lines) {
    const heading = /^(\S.*):$/.exec(line);
    if (heading) {
      current = heading[1];
      continue;
    }
    if (/^\s+Line \d+:/.test(line) && current) hit(current);
  }
  // ripgrep and grep -n style: "path:12:text".
  if (counts.size === 0) for (const line of lines) {
    const m = /^(.+?):\d+[:-]/.exec(line);
    if (m) hit(m[1]);
  }
  if (counts.size > 0) {
    const stated = /^Found (\d+) match/.exec(lines[0]);
    const matches = [...counts.values()].reduce((a, b) => a + b, 0);
    return { matches: stated ? Math.max(Number(stated[1]), matches) : matches, files: [...counts].map(([file, count]) => ({ file, count })).sort((a, b) => b.count - a.count), listing: false };
  }

  // A list of paths (glob, or grep that lists files): "Found 5 files" is optional.
  const paths = lines.filter((l) => !/^Found \d+ /.test(l) && !/^\(/.test(l));
  return paths.length > 0 && paths.every((p) => /[\\/.]/.test(p) && !/\s{2}/.test(p)) ? { matches: paths.length, files: paths.map((file) => ({ file, count: 1 })), listing: true } : undefined;
}

const plural = (n: number, word: string) => `${n} ${word}${n === 1 ? "" : "s"}`;

export const register: Register = (on) => {
  on("ui.render", { component: ["ToolUse", "ToolResult"], props: { tool: ["grep", "glob", "codesearch"] } }, async ($, e, next) => {
    if (e.component !== "ToolUse" && e.component !== "ToolResult") return next(e);
    if (e.props.status !== "completed") return next(e);
    const hits = parse(e.props.output, e.props.tool);
    if (!hits) return next(e);

    const { Box, Text, Pill } = $.ui.resolve(e);
    if (e.component === "ToolUse") {
      const label = hits.listing ? plural(hits.files.length, "file") : `${hits.matches} ${hits.matches === 1 ? "match" : "matches"} in ${plural(hits.files.length, "file")}`;
      return Pill({ tone: hits.matches === 0 ? "neutral" : "accent", label: hits.matches === 0 ? "no matches" : label });
    }
    if (hits.listing || hits.files.length < 2) return next(e);
    // Opened: the files with the most matches, then Fleet's own output under them.
    return Box({
      flexDirection: "column",
      gap: 2,
      children: [...hits.files.slice(0, 8).map((f) => Text({ code: true, children: [`${String(f.count).padStart(3)} × ${f.file}`] })), await next(e)],
    });
  });
};
