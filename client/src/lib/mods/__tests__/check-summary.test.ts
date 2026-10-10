import { describe, expect, it } from "vitest";
import type { ModCheckReport } from "@/lib/mods/kept";
import { checkProblems, summarizeCheck } from "@/lib/mods/check-summary";

function report(overrides: Partial<ModCheckReport> = {}): ModCheckReport {
  return {
    ok: true,
    name: "test-chips",
    version: "1",
    description: "Test runs as counts",
    lines: 47,
    sha256: "abc",
    hooks: [],
    calls: [],
    state: [],
    pages: [],
    errors: [],
    warnings: [],
    ...overrides,
  };
}

const keys = (r: ModCheckReport) => summarizeCheck(r).map((row) => row.key);
const text = (r: ModCheckReport, key: string) => summarizeCheck(r).find((row) => row.key === key)?.text;

describe("summarizeCheck", () => {
  it("describes the worked example: rows for bash and shell calls, nothing else but the muted rows", () => {
    const r = report({
      hooks: [{ event: "ui.render", matcher: { component: ["ToolUse", "ToolResult"], props: { tool: ["bash", "shell"] } } }],
      calls: ["ui.resolve"],
    });
    expect(keys(r)).toEqual(["draws", "reads", "changes", "network", "files"]);
    expect(text(r, "draws")).toBe("Rows for bash and shell calls (on the line, and the opened body)");
    expect(text(r, "reads")).toBe("Tool input and output of those calls");
    const muted = summarizeCheck(r).filter((row) => row.muted).map((row) => [row.key, row.text]);
    expect(muted).toEqual([["changes", "Nothing the agent sees"], ["network", "None"], ["files", "None"]]);
  });

  it("names every site, with a line each", () => {
    const r = report({
      hooks: [
        { event: "ui.render", matcher: { component: "ComposerBand" } },
        { event: "ui.render", matcher: { component: "StatusChip" } },
        { event: "ui.render", matcher: { component: "Pane" } },
      ],
    });
    const draws = summarizeCheck(r).filter((row) => row.key.startsWith("draws"));
    expect(draws.map((row) => row.text)).toEqual([
      "A band above the composer",
      "A chip in the status bar",
      "A pane it opens",
    ]);
  });

  it("says every place when there is no matcher", () => {
    expect(text(report({ hooks: [{ event: "ui.render" }] }), "draws")).toBe("Every place a mod can draw");
  });

  it("reads a regex value", () => {
    const r = report({
      hooks: [{ event: "ui.render", matcher: { component: "ToolUse", props: { tool: { $regex: "^git", flags: "i" } } } }],
    });
    expect(text(r, "draws")).toContain("matching /^git/i");
  });

  it("words a matcher with only component, one line per site and never a backtick", () => {
    const r = report({ hooks: [{ event: "ui.render", matcher: { component: ["ComposerBand", "StatusChip", "Pane"] } }] });
    const draws = summarizeCheck(r).filter((row) => row.key.startsWith("draws")).map((row) => row.text);
    expect(draws).toEqual(["A band above the composer", "A chip in the status bar", "A pane it opens"]);
    expect(summarizeCheck(r).every((row) => !row.text.includes("`"))).toBe(true);
  });

  it("falls back to compact JSON for a shape it doesn't know", () => {
    const r = report({ hooks: [{ event: "ui.render", matcher: { component: "Mystery" } }] });
    expect(text(r, "draws")).not.toContain("`");
    expect(summarizeCheck(r).find((row) => row.key === "draws")?.code).toBe('{"component":"Mystery"}');
  });

  it("lists what it listens to in one row, and omits it when nothing", () => {
    expect(keys(report())).not.toContain("listens");
    const r = report({
      hooks: [{ event: "ui.press" }, { event: "ui.input" }, { event: "session.start" }, { event: "turn.complete" }],
    });
    expect(text(r, "listens")).toBe("Buttons and fields it draws, when a session opens, when a turn ends");
  });

  it("says it reads every tool call when a render hook isn't narrowed to sites without tool rows", () => {
    const every = "Tool input and output of every tool call";
    // No matcher, tool sites not narrowed by tool, or sites Fleet can't put in words: it can draw any tool row.
    expect(text(report({ hooks: [{ event: "ui.render" }] }), "reads")).toBe(every);
    expect(text(report({ hooks: [{ event: "ui.render", matcher: { props: { status: "error" } } }] }), "reads")).toBe(every);
    expect(text(report({ hooks: [{ event: "ui.render", matcher: { component: { $regex: "^Tool" } } }] }), "reads")).toBe(every);
    expect(text(report({ hooks: [{ event: "ui.render", matcher: { component: "ToolUse" } }] }), "reads")).toBe(every);
    // Narrowed by tool name (only tool rows carry one): those calls only.
    expect(text(report({ hooks: [{ event: "ui.render", matcher: { props: { tool: "bash" } } }] }), "reads")).toBe("Tool input and output of those calls");
    // Narrowed to sites without tool rows: it reads none.
    expect(text(report({ hooks: [{ event: "ui.render", matcher: { component: ["ComposerBand", "StatusChip"] } }] }), "reads")).toBeUndefined();
  });

  it("reads session details when it calls session.*", () => {
    expect(text(report({ calls: ["session.title"] }), "reads")).toBe("Session details (title, folder, harness)");
  });

  it("joins both reads", () => {
    const r = report({ hooks: [{ event: "ui.render", matcher: { component: "ToolUse" } }], calls: ["session.title"] });
    expect(text(r, "reads")).toBe("Tool input and output of every tool call; session details (title, folder, harness)");
  });

  it("lists state keys", () => {
    expect(text(report({ state: ["count", "open"] }), "state")).toBe("Keeps per-session state: count, open");
    expect(keys(report())).not.toContain("state");
  });

  it("separates saving from reading its data", () => {
    expect(text(report({ calls: ["store.get", "store.keys"] }), "store")).toBe("Reads its saved data");
    expect(text(report({ calls: ["store.get", "store.set"] }), "store")).toBe("Saves data for you on this machine");
    expect(text(report({ calls: ["store.delete"] }), "store")).toBe("Saves data for you on this machine");
    expect(keys(report())).not.toContain("store");
  });

  it("lists its pages", () => {
    expect(text(report({ pages: ["a.html", "b.html"] }), "pages")).toBe("Shows its own pages: a.html, b.html");
  });

  it("groups the other calls in one row", () => {
    expect(text(report({ calls: ["ui.toast", "ui.open", "clock.every"] }), "also")).toBe(
      "Shows notices, opens panes, runs timers",
    );
    expect(text(report({ calls: ["clock.after"] }), "also")).toBe("Runs timers");
    expect(keys(report())).not.toContain("also");
  });

  it("keeps the order of the rows", () => {
    const r = report({
      hooks: [{ event: "ui.render", matcher: { component: "Pane" } }, { event: "ui.press" }],
      calls: ["store.set", "ui.toast", "session.title"],
      state: ["x"],
      pages: ["p.html"],
    });
    expect(keys(r)).toEqual(["draws", "listens", "reads", "state", "store", "pages", "also", "changes", "network", "files"]);
  });
});

describe("checkProblems", () => {
  it("gives the first error with its position and the warning count", () => {
    const r = report({
      ok: false,
      errors: [{ line: 12, column: 3, code: "x", message: "Call on() inside register" }, { code: "y", message: "Other" }],
      warnings: [{ code: "w", message: "w" }],
    });
    expect(checkProblems(r)).toEqual({
      error: { message: "Call on() inside register", line: 12, column: 3, where: "12:3" },
      warnings: 1,
    });
  });

  it("treats a null line or column as no position", () => {
    const r = report({ errors: [{ line: null as unknown as undefined, column: null as unknown as undefined, code: "x", message: "Bad" }] });
    expect(checkProblems(r).error?.where).toBeNull();
    const lineOnly = report({ errors: [{ line: 5, column: null as unknown as undefined, code: "x", message: "Bad" }] });
    expect(checkProblems(lineOnly).error?.where).toBe("5");
  });

  it("copes with no report and no position", () => {
    expect(checkProblems(null)).toEqual({ error: null, warnings: 0 });
    expect(checkProblems(report({ errors: [{ code: "x", message: "Bad" }] })).error).toEqual({
      message: "Bad",
      line: undefined,
      column: undefined,
      where: null,
    });
  });
});
