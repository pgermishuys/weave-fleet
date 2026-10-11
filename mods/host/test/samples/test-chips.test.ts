import { describe, expect, test } from "bun:test";
import { join } from "node:path";
import { checkMod } from "../../src/check";
import { parse } from "../../../samples/test-chips/mod";

const sample = join(import.meta.dir, "..", "..", "..", "samples", "test-chips");

describe("the test-chips sample", () => {
  test("passes the static check and hooks the two tool-row sites for shell tools", async () => {
    const { report } = await checkMod(sample);
    expect(report.errors).toEqual([]);
    expect(report.ok).toBe(true);
    expect(report.hooks).toEqual([{ event: "ui.render", matcher: { component: ["ToolUse", "ToolResult"], props: { tool: ["bash", "shell"] } } }]);
    expect(report.calls).toEqual(["ui.resolve"]);
  });

  test("dotnet test: the summary line, and the failing names", () => {
    const output = [
      "Failed WidgetTests.Rotates_the_gear [12 ms]",
      "  Failed Acme.Tests.GearTests.Spins_clockwise [3 ms]",
      "Failed!  - Failed: 2, Passed: 212, Skipped: 4, Total: 218, Duration: 9 s - Acme.Tests.dll (net10.0)",
    ].join("\n");
    expect(parse(output)).toEqual({ failed: 2, passed: 212, skipped: 4, failing: ["WidgetTests.Rotates_the_gear", "Acme.Tests.GearTests.Spins_clockwise"] });
  });

  test("dotnet test: a passing run across two assemblies adds up", () => {
    const output = [
      "Passed!  - Failed:     0, Passed:    10, Skipped:     1, Total:    11, Duration: 1 s - A.dll (net10.0)",
      "Passed!  - Failed:     0, Passed:     5, Skipped:     0, Total:     5, Duration: 1 s - B.dll (net10.0)",
    ].join("\n");
    expect(parse(output)).toEqual({ failed: 0, passed: 15, skipped: 1, failing: [] });
  });

  test("vitest: counts and failing names, with colours stripped and no repeats", () => {
    const esc = String.fromCharCode(27);
    const output = [
      ` FAIL  src/gears.test.ts > spins > clockwise`,
      ` ${esc}[31m×${esc}[39m spins > counter-clockwise 4ms`,
      ` FAIL  src/gears.test.ts > spins > clockwise`,
      `      Tests  2 failed | 212 passed | 4 skipped (218)`,
    ].join("\n");
    expect(parse(output)).toEqual({
      failed: 2,
      passed: 212,
      skipped: 4,
      failing: ["src/gears.test.ts > spins > clockwise", "spins > counter-clockwise"],
    });
    expect(parse("      Tests  3 failed (3)")).toEqual({ failed: 3, passed: 0, skipped: 0, failing: [] });
    expect(parse("      Tests  9 passed (9)")).toEqual({ failed: 0, passed: 9, skipped: 0, failing: [] });
  });

  test("bun test", () => {
    const output = ["(fail) gears > spins [1.23ms]", "(fail) gears > stops", "", " 12 pass", " 1 skip", " 2 fail", "Ran 15 tests across 3 files."].join("\n");
    expect(parse(output)).toEqual({ failed: 2, passed: 12, skipped: 1, failing: ["gears > spins", "gears > stops"] });
  });

  test("pytest", () => {
    const output = [
      "FAILED tests/test_gears.py::test_spins - assert 1 == 2",
      "FAILED tests/test_gears.py::test_stops",
      "=========== 2 failed, 212 passed, 4 skipped in 1.2s ===========",
    ].join("\n");
    expect(parse(output)).toEqual({ failed: 2, passed: 212, skipped: 4, failing: ["tests/test_gears.py::test_spins", "tests/test_gears.py::test_stops"] });
    expect(parse("===== 7 passed in 0.10s =====")).toEqual({ failed: 0, passed: 7, skipped: 0, failing: [] });
  });

  test("go test", () => {
    const output = ["--- PASS: TestSpin (0.00s)", "--- FAIL: TestStop (0.01s)", "    --- SKIP: TestStop/slow (0.00s)", "--- PASS: TestTurn (0.00s)", "FAIL"].join("\n");
    expect(parse(output)).toEqual({ failed: 1, passed: 2, skipped: 1, failing: ["TestStop"] });
  });

  test("anything else is not a test run", () => {
    expect(parse("")).toBeUndefined();
    expect(parse("total 8\ndrwxr-xr-x 2 me me 4096 Oct 10 notes")).toBeUndefined();
  });

  test("at most 50 failing names", () => {
    const lines = Array.from({ length: 60 }, (_, i) => `(fail) case ${i}`);
    expect(parse([...lines, " 0 pass", " 60 fail"].join("\n"))?.failing).toHaveLength(50);
  });
});
