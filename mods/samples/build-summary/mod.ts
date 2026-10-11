import type { Register } from "fleet-mods";

// Draws build output as error and warning counts, and the first few with file:line when the row is opened.
// Builders: dotnet build/msbuild, tsc, cargo build/check, go build.
const BUILD_COMMAND = /\b(dotnet (build|publish)|msbuild|tsc|cargo (build|check)|go (build|vet))\b/;

export type Finding = { level: "error" | "warning"; where: string; message: string };
export type Summary = { errors: number; warnings: number; findings: Finding[] };

const ANSI = new RegExp(String.fromCharCode(27) + "\\[[0-9;]*m", "g");

/** Reads a build's output. Undefined when it isn't one of the builders above (or a clean run that printed nothing). */
export function parse(raw: string, command: string): Summary | undefined {
  const lines = raw.replace(ANSI, "").split(/\r?\n/);
  const seen = new Set<string>();
  const findings: Finding[] = [];
  const add = (level: string, where: string, message: string) => {
    const key = `${level}|${where}|${message}`;
    if (seen.has(key)) return; // dotnet prints every diagnostic again in its summary
    seen.add(key);
    findings.push({ level: level.startsWith("warn") ? "warning" : "error", where, message: message.trim() });
  };

  lines.forEach((line, i) => {
    // dotnet/msbuild/tsc: "src/Cart.cs(12,5): warning CS0168: The variable 'x' is declared but never used [/p/Cart.csproj]"
    const classic = /^\s*(.+?)\((\d+)(?:,\d+)?\):\s+(error|warning)\s+(\w+):\s+(.*?)(?:\s+\[[^\]]+\])?$/.exec(line);
    if (classic) return add(classic[3], `${classic[1]}:${classic[2]}`, `${classic[4]} ${classic[5]}`);
    // go: "./main.go:12:2: declared and not used: x"
    const go = /^(\.{0,2}\/?[\w./-]+\.go):(\d+):\d+:\s+(.*)$/.exec(line);
    if (go) return add("error", `${go[1]}:${go[2]}`, go[3]);
    // cargo: "warning: unused variable: `x`" then " --> src/main.rs:3:9"
    const cargo = /^(warning|error)(?:\[(\w+)\])?:\s+(.*)$/.exec(line);
    if (cargo && !/^`|^aborting|^could not compile/.test(cargo[3])) {
      const at = /^\s*-->\s+(.+?):(\d+):\d+/.exec(lines[i + 1] ?? "");
      if (at) add(cargo[1], `${at[1]}:${at[2]}`, cargo[3]);
    }
  });

  // dotnet's own tally ("    3 Warning(s)"), when it printed one, beats counting.
  const tally = (word: string): number | undefined => {
    const m = new RegExp("^\\s*(\\d+) " + word + "\\(s\\)", "m").exec(lines.join("\n"));
    return m ? Number(m[1]) : undefined;
  };
  const count = (level: Finding["level"]) => findings.filter((f) => f.level === level).length;
  const errors = tally("Error") ?? count("error");
  const warnings = tally("Warning") ?? count("warning");
  const finished = /Build succeeded|Finished `|^\s*Found 0 errors/m.test(lines.join("\n"));
  // go and tsc print nothing when they succeed (OpenCode says "(no output)").
  const silentSuccess = /\b(tsc|go build|go vet)\b/.test(command) && /^(\(no output\))?$/.test(raw.trim());
  return findings.length > 0 || tally("Error") !== undefined || finished || silentSuccess ? { errors, warnings, findings: [...findings.filter((f) => f.level === "error"), ...findings.filter((f) => f.level === "warning")].slice(0, 50) } : undefined;
}

const plural = (n: number, word: string) => `${n} ${word}${n === 1 ? "" : "s"}`;

export const register: Register = (on) => {
  on("ui.render", { component: ["ToolUse", "ToolResult"], props: { tool: ["bash", "shell"] } }, async ($, e, next) => {
    if (e.component !== "ToolUse" && e.component !== "ToolResult") return next(e);
    if (e.props.status !== "completed" && e.props.status !== "error") return next(e); // not while it runs
    const command = String(e.props.input.command ?? "");
    if (!BUILD_COMMAND.test(command)) return next(e);
    const summary = parse(e.props.output, command);
    if (!summary) return next(e);

    const { Box, Text, Pill } = $.ui.resolve(e);
    if (e.component === "ToolUse") {
      return Box({
        flexDirection: "row",
        gap: 1,
        children: [
          Pill({ tone: summary.errors > 0 ? "bad" : "good", label: plural(summary.errors, "error") }),
          Pill({ tone: summary.warnings > 0 ? "warn" : "neutral", label: plural(summary.warnings, "warning") }),
        ],
      });
    }
    // Opened: the first eight with file:line, then Fleet's own output under them.
    return Box({
      flexDirection: "column",
      gap: 2,
      children: [
        ...summary.findings.slice(0, 8).map((f) =>
          Text({ code: true, color: f.level === "error" ? "bad" : "warn", children: [`${f.where}  ${f.message}`] }),
        ),
        await next(e),
      ],
    });
  });
};
