import { describe, expect, test } from "bun:test";
import * as mod from "../../../samples/build-summary/mod";
import { checked, fixture, pills, render, texts } from "./render";

const row = (command: string, output: string, extra = {}) => ({ input: { command }, output, ...extra });

describe("the build-summary sample", () => {
  test("passes the static check and hooks shell rows ", async () => {
    const report = await checked("build-summary");
    expect(report.hooks).toEqual([{ event: "ui.render", matcher: { component: ["ToolUse", "ToolResult"], props: { tool: ["bash", "shell"] } } }]);
  });

  test("dotnet build with three warnings (real output): counts, no repeats", () => {
    const s = mod.parse(fixture("dotnet-build-warnings.txt"), "dotnet build");
    expect(s).toMatchObject({ errors: 0, warnings: 3 });
    expect(s?.findings.map((f) => f.where)).toEqual(["/work/harbor-api/src/Berths.cs:9", "/work/harbor-api/src/Berths.cs:16", "/work/harbor-api/src/Berths.cs:15"]);
    expect(s?.findings[0].message).toBe("CS8602 Dereference of a possibly null reference.");
  });

  test("dotnet build that failed (real output)", () => {
    const s = mod.parse(fixture("dotnet-build-errors.txt"), "dotnet build");
    expect(s).toMatchObject({ errors: 2, warnings: 3 });
    expect(s?.findings.filter((f) => f.level === "error").map((f) => f.where)).toEqual(["/work/harbor-api/src/Berths.cs:17", "/work/harbor-api/src/Program.cs:5"]);
  });

  test("dotnet build with nothing to report (real output)", () => {
    expect(mod.parse(fixture("dotnet-build-clean.txt"), "dotnet build")).toMatchObject({ errors: 0, warnings: 0, findings: [] });
  });

  test("tsc: errors with file:line, and a clean run that prints nothing", () => {
    const s = mod.parse(fixture("tsc-errors.txt"), "bunx tsc --noEmit");
    expect(s).toMatchObject({ errors: 3, warnings: 0 });
    expect(s?.findings[0]).toEqual({ level: "error", where: "src/cart.ts:7", message: "TS2322 Type 'string' is not assignable to type 'number'." });
    expect(mod.parse("", "bunx tsc --noEmit")).toMatchObject({ errors: 0, warnings: 0 });
    expect(mod.parse("(no output)", "bunx tsc --noEmit")).toMatchObject({ errors: 0, warnings: 0 });
    expect(mod.parse("", "echo hi")).toBeUndefined();
  });

  test("go build (real output) reads each line as an error", () => {
    const s = mod.parse(fixture("go-build-errors.txt"), "go build ./...");
    expect(s).toMatchObject({ errors: 3, warnings: 0 });
    expect(s?.findings[1]).toEqual({ level: "error", where: "./main.go:9", message: "declared and not used: unused" });
  });

  test("cargo build: warnings and the error with their src/file:line", () => {
    const s = mod.parse(fixture("cargo-build.txt"), "cargo build");
    expect(s).toMatchObject({ errors: 1, warnings: 2 });
    expect(s?.findings.map((f) => f.where)).toEqual(["src/main.rs:8", "src/main.rs:4", "src/lib.rs:1"]);
  });

  test("other output is not a build", () => {
    expect(mod.parse("total 8\ndrwxr-xr-x 2 me me 4096 Oct 10 notes", "dotnet build")).toBeUndefined();
  });

  test("the row line shows the counts, the opened row the first findings above Fleet's body", async () => {
    const line = await render(mod, "ToolUse", row("dotnet build", fixture("dotnet-build-warnings.txt")));
    expect(pills(line)).toEqual(["0 errors", "3 warnings"]);
    const body = await render(mod, "ToolResult", row("dotnet build", fixture("dotnet-build-errors.txt")));
    expect(texts(body)[0]).toContain("Berths.cs:17");
    expect(texts(body)).toHaveLength(5);
    expect(await render(mod, "ToolUse", row("ls", fixture("dotnet-build-warnings.txt")))).toBe("next");
  });
});
