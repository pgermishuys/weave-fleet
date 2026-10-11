import type { Register } from "fleet-mods";

const TEST_COMMAND = /\b(dotnet test|bun (run )?test|vitest|pytest|go test)\b/;

type Counts = { passed: number; failed: number; skipped: number; failing: string[] };

function parse(output: string): Counts | undefined {
  // dotnet test: "Failed!  - Failed: 2, Passed: 212, Skipped: 4, Total: 218"
  const dotnet = /Failed:\s*(\d+),\s*Passed:\s*(\d+),\s*Skipped:\s*(\d+)/.exec(output);
  // vitest: "Tests  2 failed | 212 passed | 4 skipped (218)"
  const vitest = /Tests\s+(?:(\d+) failed \| )?(\d+) passed(?: \| (\d+) skipped)?/.exec(output);
  const failing = [...output.matchAll(/^\s*(?:Failed|FAIL|×)\s+(\S.*)$/gm)].map((m) => m[1].trim()).slice(0, 50);
  if (dotnet) return { failed: +dotnet[1], passed: +dotnet[2], skipped: +dotnet[3], failing };
  if (vitest) return { failed: +(vitest[1] ?? 0), passed: +vitest[2], skipped: +(vitest[3] ?? 0), failing };
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
