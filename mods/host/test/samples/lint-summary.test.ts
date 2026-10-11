import { describe, expect, test } from "bun:test";
import * as mod from "../../../samples/lint-summary/mod";
import { checked, fixture, pills, render, texts } from "./render";

describe("the lint-summary sample", () => {
  test("passes the static check and hooks shell rows ", async () => {
    const report = await checked("lint-summary");
    expect(report.hooks).toEqual([{ event: "ui.render", matcher: { component: ["ToolUse", "ToolResult"], props: { tool: ["bash", "shell"] } } }]);
  });

  test("ESLint stylish output (real): the footer's counts, and the rules most-hit first", () => {
    const lint = mod.parse(fixture("eslint-stylish.txt"));
    expect(lint?.errors).toBe(5);
    expect(lint?.warnings).toBe(3);
    expect(lint?.rules[0]).toEqual({ rule: "no-console", count: 3, level: "warning" });
    expect(lint?.rules.map((r) => r.rule)).toEqual(["no-console", "eqeqeq", "@typescript-eslint/no-explicit-any", "@typescript-eslint/no-unused-vars", "no-var"]);
  });

  test("ruff, in its full and its concise format (real output)", () => {
    for (const file of ["ruff-full.txt", "ruff-concise.txt"]) {
      const lint = mod.parse(fixture(file));
      expect(lint).toMatchObject({ errors: 5, warnings: 0 });
      expect(lint?.rules).toEqual([
        { rule: "F401", count: 2, level: "error" },
        { rule: "E722", count: 1, level: "error" },
        { rule: "F541", count: 1, level: "error" },
        { rule: "F841", count: 1, level: "error" },
      ]);
    }
  });

  test("clean runs and other output", () => {
    expect(mod.parse("All checks passed!\n")).toMatchObject({ errors: 0, warnings: 0, rules: [] });
    expect(mod.parse("total 8")).toBeUndefined();
  });

  test("the row line and the opened rule list", async () => {
    const row = { input: { command: "bunx eslint src" }, output: fixture("eslint-stylish.txt") };
    expect(pills(await render(mod, "ToolUse", row))).toEqual(["5 errors", "3 warnings"]);
    const body = await render(mod, "ToolResult", row);
    expect(texts(body)).toEqual(["  3 × no-console", "  2 × eqeqeq", "  1 × @typescript-eslint/no-explicit-any", "  1 × @typescript-eslint/no-unused-vars", "  1 × no-var"]);
    expect(await render(mod, "ToolUse", { input: { command: "ls" }, output: "" })).toBe("next");
  });
});
