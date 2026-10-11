import type { Register } from "fleet-mods";

// Draws test runs as passed, failed and skipped counts, and the failing names when the row is opened.
// Runners: dotnet test, vitest, bun test, pytest, go test.
const TEST_COMMAND = /\b(dotnet test|bun (run )?test|bunx vitest|npx vitest|vitest|pytest|go test)\b/;

export type Counts = { passed: number; failed: number; skipped: number; failing: string[] };

const ANSI = new RegExp(String.fromCharCode(27) + "\\[[0-9;]*m", "g");

const sum = (text: string, pattern: RegExp, group: number): number =>
  [...text.matchAll(pattern)].reduce((total, m) => total + Number(m[group] ?? 0), 0);

const names = (text: string, pattern: RegExp): string[] =>
  [...new Set([...text.matchAll(pattern)].map((m) => (m[1] ?? "").trim()).filter((name) => name !== ""))];

const count = (line: string, word: string): number => Number(new RegExp("(\\d+) " + word).exec(line)?.[1] ?? 0);

/** Reads a test run's output. Undefined when it isn't one of the runners above. */
export function parse(raw: string): Counts | undefined {
  const output = raw.replace(ANSI, "");
  const found = (counts: Omit<Counts, "failing">, failing: string[]): Counts => ({ ...counts, failing: failing.slice(0, 50) });

  // dotnet test: "Failed!  - Failed: 2, Passed: 212, Skipped: 4, Total: 218", once per test assembly.
  const dotnet = /^(?:Passed|Failed)!\s+-\s+Failed:\s*(\d+),\s*Passed:\s*(\d+),\s*Skipped:\s*(\d+)/gm;
  if (output.match(dotnet)) {
    return found(
      { failed: sum(output, dotnet, 1), passed: sum(output, dotnet, 2), skipped: sum(output, dotnet, 3) },
      names(output, /^\s*Failed\s+(\S.*?)\s+\[[\d.,]+\s*m?s\]\s*$/gm),
    );
  }

  // vitest: "Tests  2 failed | 212 passed | 4 skipped (218)"
  const vitest = /^\s*Tests\s+(.*?)\s*\(\d+\)\s*$/m.exec(output);
  if (vitest) {
    const line = vitest[1];
    return found(
      { failed: count(line, "failed"), passed: count(line, "passed"), skipped: count(line, "skipped") },
      names(output, /^\s*(?:FAIL|×)\s+(\S.*?)(?:\s+\d+ms)?\s*$/gm),
    );
  }

  // pytest: "=== 2 failed, 212 passed, 4 skipped in 1.2s ==="
  const pytest = /^=+ (.*\b(?:passed|failed|errors?|skipped)\b.*?) in [\d.]+s/m.exec(output);
  if (pytest) {
    const line = pytest[1];
    return found(
      { failed: count(line, "failed") + count(line, "errors?"), passed: count(line, "passed"), skipped: count(line, "skipped") },
      names(output, /^FAILED\s+(\S+)/gm),
    );
  }

  // bun test: " 12 pass", " 2 fail", " 1 skip"
  const pass = /^\s*(\d+) pass\s*$/m.exec(output);
  const fail = /^\s*(\d+) fail\s*$/m.exec(output);
  if (pass || fail) {
    const skip = /^\s*(\d+) skip\s*$/m.exec(output);
    return found(
      { failed: Number(fail?.[1] ?? 0), passed: Number(pass?.[1] ?? 0), skipped: Number(skip?.[1] ?? 0) },
      names(output, /^\(fail\)\s+(.+?)(?:\s+\[[\d.]+m?s\])?\s*$/gm),
    );
  }

  // go test: one "--- PASS:", "--- FAIL:" or "--- SKIP:" line per test (and per subtest).
  const tally = (verdict: string): number => [...output.matchAll(new RegExp("^\\s*--- " + verdict + ":", "gm"))].length;
  const go = { failed: tally("FAIL"), passed: tally("PASS"), skipped: tally("SKIP") };
  if (go.failed + go.passed + go.skipped > 0) return found(go, names(output, /^\s*--- FAIL:\s+(\S+)/gm));
  return undefined;
}

export const register: Register = (on) => {
  on("ui.render", { component: ["ToolUse", "ToolResult"], props: { tool: ["bash", "shell"] } }, async ($, e, next) => {
    if (e.component !== "ToolUse" && e.component !== "ToolResult") return next(e);
    if (!TEST_COMMAND.test(String(e.props.input.command ?? ""))) return next(e);
    const counts = parse(e.props.output);
    if (!counts) return next(e);

    const { Box, Text, Pill } = $.ui.resolve(e);
    const pills = Box({
      flexDirection: "row",
      gap: 1,
      children: [
        Pill({ tone: "good", label: `${counts.passed} passed` }),
        counts.failed > 0 && Pill({ tone: "bad", label: `${counts.failed} failed` }),
        counts.skipped > 0 && Pill({ tone: "neutral", label: `${counts.skipped} skipped` }),
      ],
    });
    if (e.component === "ToolUse") return pills;

    // Opened: the failing names, then Fleet's own output under them.
    return Box({
      flexDirection: "column",
      gap: 2,
      children: [
        ...counts.failing.map((name) => Text({ code: true, color: "bad", children: [name] })),
        await next(e),
      ],
    });
  });
};
