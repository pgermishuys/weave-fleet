import type { Register } from "fleet-mods";

// Draws an ESLint (stylish) or ruff run as error and warning counts, and the problems grouped by rule when the row is opened.
const LINT_COMMAND = /\b(eslint|ruff|lint)\b/;

export type Lint = { errors: number; warnings: number; rules: { rule: string; count: number; level: "error" | "warning" }[] };

const ANSI = new RegExp(String.fromCharCode(27) + "\\[[0-9;]*m", "g");

/** Reads a lint run's output. Undefined when it isn't ESLint's stylish format or ruff's. */
export function parse(raw: string): Lint | undefined {
  const lines = raw.replace(ANSI, "").split(/\r?\n/);
  const byRule = new Map<string, { count: number; level: "error" | "warning" }>();
  const tally = (rule: string, level: "error" | "warning") => {
    const entry = byRule.get(rule) ?? { count: 0, level };
    entry.count += 1;
    if (level === "error") entry.level = "error";
    byRule.set(rule, entry);
  };

  for (const line of lines) {
    // ESLint stylish: "  12:7  error  'x' is assigned a value but never used  @typescript-eslint/no-unused-vars"
    const eslint = /^\s+\d+:\d+\s+(error|warning)\s+.*?\s{2,}(\S+)\s*$/.exec(line);
    if (eslint) tally(eslint[2], eslint[1] as "error" | "warning");
    // ruff, concise: "src/cart.py:3:8: F401 [*] `os` imported but unused" (new full format: "F401 [*] `os` imported but unused")
    const ruff = /^(?:\S+:\d+:\d+: )?([A-Z]{1,4}\d{2,4}) (?:\[[*\w ]+\] )?\S/.exec(line);
    if (ruff && (/:\d+:\d+: /.test(line) || /^\s*--> /.test(lines[lines.indexOf(line) + 1] ?? ""))) tally(ruff[1], "error");
  }

  const summary = /^\s*✖ (\d+) problems? \((\d+) errors?, (\d+) warnings?\)/m.exec(lines.join("\n"));
  const ruffTotal = /^Found (\d+) errors?/m.exec(lines.join("\n"));
  const clean = /^All checks passed!/m.test(lines.join("\n"));
  if (byRule.size === 0 && !summary && !ruffTotal && !clean) return undefined;
  const sum = (level: "error" | "warning") => [...byRule.values()].filter((r) => r.level === level).reduce((n, r) => n + r.count, 0);
  return {
    errors: summary ? Number(summary[2]) : sum("error"),
    warnings: summary ? Number(summary[3]) : sum("warning"),
    rules: [...byRule].map(([rule, v]) => ({ rule, ...v })).sort((a, b) => b.count - a.count || a.rule.localeCompare(b.rule)),
  };
}

const plural = (n: number, word: string) => `${n} ${word}${n === 1 ? "" : "s"}`;

export const register: Register = (on) => {
  on("ui.render", { component: ["ToolUse", "ToolResult"], props: { tool: ["bash", "shell"] } }, async ($, e, next) => {
    if (e.component !== "ToolUse" && e.component !== "ToolResult") return next(e);
    if (e.props.status !== "completed" && e.props.status !== "error") return next(e); // not while it runs
    if (!LINT_COMMAND.test(String(e.props.input.command ?? ""))) return next(e);
    const lint = parse(e.props.output);
    if (!lint) return next(e);

    const { Box, Text, Pill } = $.ui.resolve(e);
    if (e.component === "ToolUse") {
      return Box({
        flexDirection: "row",
        gap: 1,
        children: [
          Pill({ tone: lint.errors > 0 ? "bad" : "good", label: plural(lint.errors, "error") }),
          Pill({ tone: lint.warnings > 0 ? "warn" : "neutral", label: plural(lint.warnings, "warning") }),
        ],
      });
    }
    // Opened: the rules, most-hit first, then Fleet's own output under them.
    return Box({
      flexDirection: "column",
      gap: 2,
      children: [
        ...lint.rules.slice(0, 10).map((r) => Text({ code: true, color: r.level === "error" ? "bad" : "warn", children: [`${String(r.count).padStart(3)} × ${r.rule}`] })),
        await next(e),
      ],
    });
  });
};
