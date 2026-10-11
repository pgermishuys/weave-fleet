import { describe, expect, test } from "bun:test";
import * as mod from "../../../samples/search-hits/mod";
import { checked, fixture, pills, render, texts } from "./render";

describe("the search-hits sample", () => {
  test("passes the static check and hooks Fleet's search tools", async () => {
    const report = await checked("search-hits");
    expect(report.hooks).toEqual([{ event: "ui.render", matcher: { component: ["ToolUse", "ToolResult"], props: { tool: ["grep", "glob", "codesearch"] } } }]);
  });

  test("OpenCode's grep output (real): matches per file, most first", () => {
    const hits = mod.parse(fixture("grep-export-function.txt"), "grep");
    expect(hits).toEqual({
      matches: 5,
      listing: false,
      files: [{ file: "/work/cart-totals/src/totals.ts", count: 4 }, { file: "/work/cart-totals/src/cart.ts", count: 1 }],
    });
    expect(mod.parse(fixture("grep-console-log.txt"), "grep")).toMatchObject({ matches: 3, files: [{ count: 3 }] });
  });

  test("glob output (real) is a list of files", () => {
    expect(mod.parse(fixture("glob-ts.txt"), "glob")).toMatchObject({ matches: 2, listing: true });
  });

  test("path:line:text lines, and no results", () => {
    expect(mod.parse("src/a.ts:3:foo\nsrc/a.ts:9:foo\nsrc/b.ts:1:foo", "grep")).toMatchObject({ matches: 3, files: [{ file: "src/a.ts", count: 2 }, { file: "src/b.ts", count: 1 }] });
    expect(mod.parse("No files found", "glob")).toEqual({ matches: 0, files: [], listing: true });
    expect(mod.parse("Found 2 matches\nsomething odd  with spaces", "grep")).toBeUndefined();
  });

  test("the row line and the opened per-file list", async () => {
    const grep = { tool: "grep", output: fixture("grep-export-function.txt") };
    expect(pills(await render(mod, "ToolUse", grep))).toEqual(["5 matches in 2 files"]);
    expect(texts(await render(mod, "ToolResult", grep))).toEqual(["  4 × /work/cart-totals/src/totals.ts", "  1 × /work/cart-totals/src/cart.ts"]);
    const glob = { tool: "glob", output: fixture("glob-ts.txt") };
    expect(pills(await render(mod, "ToolUse", glob))).toEqual(["2 files"]);
    expect(await render(mod, "ToolResult", glob)).toBe("next");
    expect(pills(await render(mod, "ToolUse", { tool: "grep", output: "No matches found" }))).toEqual(["no matches"]);
    expect(await render(mod, "ToolUse", { ...grep, status: "running" })).toBe("next");
  });
});
