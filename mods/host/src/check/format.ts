import type { CheckProblem, CheckReport } from "fleet-mods/protocol";
import type { CheckReportHook } from "fleet-mods/protocol-shared";

type Value = unknown;

const IDENTIFIER = /^[A-Za-z_$][\w$]*$/;

function isRegex(value: Value): value is { $regex: string; flags: string } {
  return typeof value === "object" && value !== null && !Array.isArray(value) && "$regex" in value;
}

function format(value: Value): string {
  if (isRegex(value)) return `/${value.$regex}/${value.flags ?? ""}`;
  if (Array.isArray(value)) return `[${value.map(format).join(",")}]`;
  if (typeof value === "object" && value !== null) {
    const entries = Object.entries(value).map(([k, v]) => `${IDENTIFIER.test(k) ? k : JSON.stringify(k)}: ${format(v)}`);
    return entries.length === 0 ? "{}" : `{ ${entries.join(", ")} }`;
  }
  return JSON.stringify(value) ?? "undefined";
}

function formatHook(hook: CheckReportHook): string {
  const matcher = hook.matcher;
  if (matcher === undefined) return hook.event;
  if (hook.event === "ui.render" && typeof matcher === "object" && matcher !== null && !Array.isArray(matcher) && "component" in matcher) {
    const { component, ...rest } = matcher as Record<string, Value>;
    const tail = Object.keys(rest).length > 0 ? ` ${format(rest)}` : "";
    return `${hook.event} ${format(component)}${tail}`;
  }
  return `${hook.event} ${format(matcher)}`;
}

const list = (items: string[]) => (items.length === 0 ? "(none)" : items.join(", "));

function problemLine(kind: "error" | "warning", p: CheckProblem): string {
  const at = p.line === undefined ? "-" : `${p.line}:${p.column ?? 1}`;
  return `${kind.padEnd(8)}${at}  ${p.code}  ${p.message}`;
}

/** The report as the text Keep and `fleet_mod_check` show. */
export function formatReport(report: CheckReport): string {
  const lines = [
    `${report.name} ${report.version} · ${report.lines} ${report.lines === 1 ? "line" : "lines"}`,
    `hooks:  ${report.hooks.length === 0 ? "(none)" : report.hooks.map(formatHook).join("\n        ")}`,
    `calls:  ${list(report.calls)}`,
    `state:  ${list(report.state)}`,
    `pages:  ${list(report.pages)}`,
  ];
  for (const p of report.errors) lines.push(problemLine("error", p));
  for (const p of report.warnings) lines.push(problemLine("warning", p));
  return lines.join("\n");
}
