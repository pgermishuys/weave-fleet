import type { ModCheckHook, ModCheckReport } from "@/lib/mods/kept";

/** One line of the review dialog's "what this mod touches" list. */
export interface CheckRow {
  key: string;
  text: string;
  /** The Stage 1 constants ("None"): shown quietly. */
  muted?: boolean;
}

const SITE_WORDS: Record<string, string> = {
  ComposerBand: "A band above the composer",
  StatusChip: "A chip in the status bar",
  Pane: "A pane it opens",
};

const LISTENS: Record<string, string> = {
  "ui.press": "Buttons and fields it draws",
  "ui.input": "Buttons and fields it draws",
  "ui.select": "Buttons and fields it draws",
  "session.start": "When a session opens",
  "turn.complete": "When a turn ends",
};

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

/** The values a matcher field allows, as plain words; null when the field isn't a shape we know. */
function words(value: unknown): string[] | null {
  if (typeof value === "string") return [value];
  if (isRecord(value) && typeof value.$regex === "string") {
    const flags = typeof value.flags === "string" ? value.flags : "";
    return [`matching /${value.$regex}/${flags}`];
  }
  if (Array.isArray(value) && value.length > 0) {
    const all: string[] = [];
    for (const item of value) {
      const inner = typeof item === "string" || (isRecord(item) && typeof item.$regex === "string") ? words(item) : null;
      if (!inner) return null;
      all.push(...inner);
    }
    return all;
  }
  return null;
}

function list(items: string[]): string {
  return items.length <= 1 ? (items[0] ?? "") : `${items.slice(0, -1).join(", ")} and ${items.at(-1)}`;
}

function raw(matcher: unknown): string {
  return `\`${JSON.stringify(matcher)}\``;
}

function describeRender(matcher: unknown): string {
  if (matcher === undefined || matcher === null) return "Every place a mod can draw";
  if (!isRecord(matcher)) return raw(matcher);
  const components = words(matcher.component);
  const keys = Object.keys(matcher);
  if (!components || keys.some((key) => key !== "component" && key !== "props")) return raw(matcher);

  const props = matcher.props;
  const propKeys = isRecord(props) ? Object.keys(props) : [];
  const toolRows = components.every((name) => name === "ToolUse" || name === "ToolResult");
  if (toolRows) {
    const both = components.includes("ToolUse") && components.includes("ToolResult");
    const where = both ? " (on the line, and the opened body)" : components[0] === "ToolUse" ? " (on the line)" : " (in the opened body)";
    if (matcher.props === undefined) return `Rows for every tool call${where}`;
    const tools = isRecord(props) && propKeys.length === 1 && propKeys[0] === "tool" ? words(props.tool) : null;
    if (!tools) return raw(matcher);
    return `Rows for ${list(tools)} calls${where}`;
  }
  if (matcher.props === undefined && components.length === 1 && SITE_WORDS[components[0]!]) return SITE_WORDS[components[0]!]!;
  return raw(matcher);
}

function hasHook(report: ModCheckReport, event: string): boolean {
  return report.hooks.some((hook) => hook.event === event);
}

function drawsToolRows(hook: ModCheckHook): boolean {
  if (hook.event !== "ui.render") return false;
  const components = isRecord(hook.matcher) ? words(hook.matcher.component) : null;
  return components?.some((name) => name === "ToolUse" || name === "ToolResult") ?? false;
}

/** What a mod touches, in plain words, from the static check's report. Rows that don't apply are left out. */
export function summarizeCheck(report: ModCheckReport): CheckRow[] {
  const rows: CheckRow[] = [];
  const calls = new Set(report.calls);
  const callsOf = (prefix: string) => report.calls.some((call) => call.startsWith(prefix));

  const renders = report.hooks.filter((hook) => hook.event === "ui.render");
  renders.forEach((hook, index) => {
    rows.push({ key: index === 0 ? "draws" : `draws-${index}`, text: describeRender(hook.matcher) });
  });

  const listens = [...new Set(report.hooks.map((hook) => LISTENS[hook.event]).filter((word): word is string => !!word))];
  if (listens.length > 0) {
    rows.push({ key: "listens", text: listens.map((word, i) => (i === 0 ? word : word.charAt(0).toLowerCase() + word.slice(1))).join(", ") });
  }

  const reads: string[] = [];
  if (report.hooks.some(drawsToolRows)) reads.push("Tool input and output of those calls");
  if (callsOf("session.")) reads.push("Session details (title, folder, harness)");
  if (reads.length > 0) rows.push({ key: "reads", text: reads.map((r, i) => (i === 0 ? r : r.charAt(0).toLowerCase() + r.slice(1))).join("; ") });

  if (report.state.length > 0) rows.push({ key: "state", text: `Keeps per-session state: ${report.state.join(", ")}` });

  if (calls.has("store.set") || calls.has("store.delete")) {
    rows.push({ key: "store", text: "Saves data for you on this machine" });
  } else if (calls.has("store.get") || calls.has("store.keys")) {
    rows.push({ key: "store", text: "Reads its saved data" });
  }

  if (report.pages.length > 0) rows.push({ key: "pages", text: `Shows its own pages: ${report.pages.join(", ")}` });

  const also: string[] = [];
  if (calls.has("ui.toast")) also.push("Shows notices");
  if (calls.has("ui.open")) also.push("Opens panes");
  if (calls.has("clock.every") || calls.has("clock.after")) also.push("Runs timers");
  if (also.length > 0) rows.push({ key: "also", text: also.map((r, i) => (i === 0 ? r : r.charAt(0).toLowerCase() + r.slice(1))).join(", ") });

  // Stage 1 mods can't do these; the rows say so instead of leaving the question open.
  rows.push({ key: "changes", text: "Nothing the agent sees", muted: true });
  rows.push({ key: "network", text: "None", muted: true });
  rows.push({ key: "files", text: "None", muted: true });
  return rows;
}

/** The first error (with where it is) and how many warnings, for the card and the dialog. */
export function checkProblems(report: ModCheckReport | null | undefined): {
  error: { message: string; line: number | undefined; column: number | undefined; where: string | null } | null;
  warnings: number;
} {
  if (!report) return { error: null, warnings: 0 };
  const first = report.errors[0];
  const where = first?.line === undefined ? null : first.column === undefined ? `${first.line}` : `${first.line}:${first.column}`;
  return {
    error: first ? { message: first.message, line: first.line, column: first.column, where } : null,
    warnings: report.warnings.length,
  };
}
