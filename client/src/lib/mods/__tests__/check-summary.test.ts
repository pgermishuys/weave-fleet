import { describe, expect, it } from "vitest";
import type { ModCheckHook, ModCheckReport } from "@/lib/mods/kept";
import { checkProblems, summarizeCheck } from "@/lib/mods/check-summary";

const report = (overrides: Partial<ModCheckReport> = {}): ModCheckReport => ({
  ok: true, name: "test-chips", version: "1", description: "", lines: 47, sha256: "abc",
  hooks: [], calls: [], state: [], pages: [], errors: [], warnings: [], ...overrides,
});
const render = (matcher?: unknown): ModCheckHook => ({ event: "ui.render", matcher });
const withHooks = (...hooks: ModCheckHook[]) => report({ hooks });

const keys = (r: ModCheckReport) => summarizeCheck(r).map((row) => row.key);
const text = (r: ModCheckReport, key: string) => summarizeCheck(r).find((row) => row.key === key)?.text;

describe("summarizeCheck", () => {
  it("describes the worked example: rows for bash and shell calls, then the muted rows", () => {
    const r = report({
      hooks: [render({ component: ["ToolUse", "ToolResult"], props: { tool: ["bash", "shell"] } })],
      calls: ["ui.resolve"],
    });
    expect(keys(r)).toEqual(["draws", "reads", "changes", "network", "files"]);
    expect(text(r, "draws")).toBe("Rows for bash and shell calls (on the line, and the opened body)");
    expect(text(r, "reads")).toBe("Tool input and output of those calls");
    expect(summarizeCheck(r).filter((row) => row.muted).map((row) => row.text)).toEqual(["Nothing the agent sees", "None", "None"]);
  });

  it("names every site, one row each, and every place when there is no matcher", () => {
    const sites = summarizeCheck(withHooks(render({ component: ["ComposerBand", "StatusChip", "Pane"] })));
    expect(sites.filter((row) => row.key.startsWith("draws")).map((row) => row.text)).toEqual([
      "A band above the composer", "A chip in the status bar", "A pane it opens",
    ]);
    expect(text(withHooks(render()), "draws")).toBe("Every place a mod can draw");
  });

  it("narrows by tool with a regex, and falls back to code for a shape it doesn't know", () => {
    const regex = withHooks(render({ component: "ToolUse", props: { tool: { $regex: "^git", flags: "i" } } }));
    expect(text(regex, "draws")).toContain("matching /^git/i");
    const unknown = summarizeCheck(withHooks(render({ component: "Mystery" }))).find((row) => row.key === "draws");
    expect(unknown?.code).toBe('{"component":"Mystery"}');
    expect(unknown?.text).not.toContain("`");
  });

  it("lists what it listens to in one row, and omits it when nothing", () => {
    expect(keys(report())).not.toContain("listens");
    const r = withHooks({ event: "ui.press" }, { event: "ui.input" }, { event: "session.start" }, { event: "turn.complete" });
    expect(text(r, "listens")).toBe("Buttons and fields it draws, when a session opens, when a turn ends");
  });

  it("says it reads every tool call unless the render hook is narrowed by tool or to sites without tool rows", () => {
    const every = "Tool input and output of every tool call";
    expect(text(withHooks(render()), "reads")).toBe(every);
    expect(text(withHooks(render({ props: { status: "error" } })), "reads")).toBe(every);
    expect(text(withHooks(render({ component: "ToolUse" })), "reads")).toBe(every);
    expect(text(withHooks(render({ props: { tool: "bash" } })), "reads")).toBe("Tool input and output of those calls");
    expect(text(withHooks(render({ component: ["ComposerBand", "StatusChip"] })), "reads")).toBeUndefined();
  });

  it("reads session details when it calls session.*", () => {
    expect(text(report({ calls: ["session.title"] }), "reads")).toBe("Session details (title, folder, harness)");
  });

  it("lists state, saved data, pages and the other calls", () => {
    expect(text(report({ state: ["count", "open"] }), "state")).toBe("Keeps per-session state: count, open");
    expect(text(report({ calls: ["store.get"] }), "store")).toBe("Reads its saved data");
    expect(text(report({ calls: ["store.get", "store.set"] }), "store")).toBe("Saves data for you on this machine");
    expect(text(report({ pages: ["a.html", "b.html"] }), "pages")).toBe("Shows its own pages: a.html, b.html");
    expect(text(report({ calls: ["ui.toast", "ui.open", "clock.every"] }), "also")).toBe("Shows notices, opens panes, runs timers");
    expect(keys(report())).toEqual(["changes", "network", "files"]);
  });

  it("keeps the order of the rows", () => {
    const r = report({ hooks: [render({ component: "Pane" }), { event: "ui.press" }], calls: ["store.set", "ui.toast", "session.title"], state: ["x"], pages: ["p.html"] });
    expect(keys(r)).toEqual(["draws", "listens", "reads", "state", "store", "pages", "also", "changes", "network", "files"]);
  });
});

describe("checkProblems", () => {
  it("gives the first error with its position and the warning count", () => {
    const r = report({
      errors: [{ line: 12, column: 3, code: "x", message: "Call on() inside register" }, { code: "y", message: "Other" }],
      warnings: [{ code: "w", message: "w" }],
    });
    expect(checkProblems(r)).toEqual({ error: { message: "Call on() inside register", line: 12, column: 3, where: "12:3" }, warnings: 1 });
  });

  it("copes with no report and no position", () => {
    expect(checkProblems(null)).toEqual({ error: null, warnings: 0 });
    expect(checkProblems(report({ errors: [{ code: "x", message: "Bad" }] })).error?.where).toBeNull();
  });
});
