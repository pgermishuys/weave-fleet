import { beforeEach, describe, expect, it } from "vitest";
import type { AccumulatedMessage, AccumulatedPart, DelegationDto } from "@/lib/client-types";
import {
  clearTurnDerivationCache,
  deriveTurns,
  modelDisplayName,
  parseUnifiedDiff,
  turnTotals,
} from "@/lib/turns";

let partCounter = 0;

function text(body: string): AccumulatedPart {
  partCounter += 1;
  return { partId: `text-${partCounter}`, type: "text", text: body };
}

function tool(
  name: string,
  state: Record<string, unknown>,
  callId = `call-${(partCounter += 1)}`,
): AccumulatedPart {
  return { partId: `tool-${callId}`, type: "tool", tool: name, callId, state };
}

function user(id: string, body: string, createdAt = 1_000): AccumulatedMessage {
  return { messageId: id, sessionId: "s1", role: "user", parts: [text(body)], createdAt };
}

function assistant(
  id: string,
  parts: AccumulatedPart[],
  extra: Partial<AccumulatedMessage> = {},
): AccumulatedMessage {
  return {
    messageId: id,
    sessionId: "s1",
    role: "assistant",
    parts,
    createdAt: 2_000,
    agent: "build",
    modelID: "claude-opus-5",
    ...extra,
  };
}

beforeEach(() => {
  partCounter = 0;
  clearTurnDerivationCache();
});

describe("round boundaries", () => {
  it("starts a round at each user message and runs it to the next one", () => {
    const turns = deriveTurns([
      user("u1", "First ask", 1_000),
      assistant("a1", [text("working")], { createdAt: 1_100 }),
      assistant("a2", [text("still working")], { createdAt: 1_200 }),
      user("u2", "Second ask", 2_000),
      assistant("a3", [text("done")], { createdAt: 2_100 }),
    ]);

    expect(turns).toHaveLength(2);
    // Newest first, matching the sidebar.
    expect(turns[0]!.prompt).toBe("Second ask");
    expect(turns[0]!.number).toBe(2);
    expect(turns[1]!.prompt).toBe("First ask");
    expect(turns[1]!.number).toBe(1);
    expect(turns[1]!.id).toBe("u1");
  });

  it("shows the prompt's first line only, and keeps the whole prompt for the tooltip", () => {
    const [turn] = deriveTurns([user("u1", "Fix the header\n\nIt wraps on phones."), assistant("a1", [text("ok")])]);

    expect(turn!.prompt).toBe("Fix the header");
    expect(turn!.promptFull).toContain("It wraps on phones.");
  });

  it("keeps assistant messages older than the loaded history in a round with no prompt", () => {
    const turns = deriveTurns([
      assistant("a0", [text("tail of an earlier round")], { createdAt: 500 }),
      user("u1", "Next ask", 1_000),
      assistant("a1", [text("ok")], { createdAt: 1_100 }),
    ]);

    expect(turns).toHaveLength(2);
    expect(turns[1]!.hasPrompt).toBe(false);
    expect(turns[0]!.hasPrompt).toBe(true);
  });

  it("gives a prompt sent twice in a row its own round each time", () => {
    const turns = deriveTurns([user("u1", "One", 1_000), user("u2", "Two", 1_100), assistant("a1", [text("ok")])]);

    expect(turns.map((turn) => turn.prompt)).toEqual(["Two", "One"]);
    expect(turns[1]!.toolCount).toBe(0);
  });
});

describe("the model that ran the round", () => {
  it("takes the last model and flags a round that changed model", () => {
    const [turn] = deriveTurns([
      user("u1", "Drop to Sonnet for the rest"),
      assistant("a1", [text("ok")], { modelID: "claude-opus-5" }),
      assistant("a2", [text("carrying on")], { modelID: "claude-sonnet-5" }),
    ]);

    expect(turn!.modelId).toBe("claude-sonnet-5");
    expect(turn!.modelChanged).toBe(true);
  });

  it("does not flag a round that stayed on one model", () => {
    const [turn] = deriveTurns([
      user("u1", "Carry on"),
      assistant("a1", [text("ok")], { modelID: "claude-opus-5" }),
      assistant("a2", [text("more")], { modelID: "claude-opus-5" }),
    ]);

    expect(turn!.modelChanged).toBe(false);
    expect(turn!.modelId).toBe("claude-opus-5");
  });

  it("resolves a model id to its catalog name, qualified or bare", () => {
    const models = [{ id: "claude-opus-5", name: "Opus 5" }];

    expect(modelDisplayName("claude-opus-5", models)).toBe("Opus 5");
    expect(modelDisplayName("anthropic/claude-opus-5", models)).toBe("Opus 5");
    expect(modelDisplayName("some-unknown-model", models)).toBe("some-unknown-model");
    expect(modelDisplayName(undefined, models)).toBe("Unknown model");
  });
});

describe("what a round wrote", () => {
  it("reads a file and its +/- from the diff the harness attached", () => {
    const [turn] = deriveTurns([
      user("u1", "Fix the status"),
      assistant("a1", [
        tool("edit", {
          status: "completed",
          input: { filePath: "src/WeaveFleet.Application/SessionStatusDeriver.cs" },
          diffLines: [
            { type: "remove", content: "return SessionStatus.Idle;", oldLineNumber: 118 },
            { type: "add", content: "return pendingQuestion is not null", newLineNumber: 118 },
            { type: "add", content: "    ? SessionStatus.WaitingInput", newLineNumber: 119 },
          ],
        }),
      ]),
    ]);

    expect(turn!.files).toHaveLength(1);
    const [file] = turn!.files;
    expect(file!.name).toBe("SessionStatusDeriver.cs");
    expect(file!.dir).toBe("src/WeaveFleet.Application");
    expect(file!.additions).toBe(2);
    expect(file!.deletions).toBe(1);
    expect(turn!.additions).toBe(2);
    expect(turn!.deletions).toBe(1);
    expect(turn!.readOnly).toBe(false);
    expect(turn!.editCount).toBe(1);
  });

  it("parses a unified diff the harness left on the call's metadata", () => {
    const [turn] = deriveTurns([
      user("u1", "Patch it"),
      assistant("a1", [
        tool("edit", {
          status: "completed",
          input: { filePath: "client/src/lib/turns.ts" },
          metadata: {
            diff: ["@@ -10,2 +10,3 @@", " const a = 1;", "-const b = 2;", "+const b = 3;", "+const c = 4;"].join("\n"),
          },
        }),
      ]),
    ]);

    const [file] = turn!.files;
    expect(file!.additions).toBe(2);
    expect(file!.deletions).toBe(1);
    expect(file!.diff.filter((line) => line.type === "context")).toHaveLength(1);
  });

  it("works the lines out from the input when no diff came with the call", () => {
    const [turn] = deriveTurns([
      user("u1", "Rename it"),
      assistant("a1", [
        tool("Edit", {
          status: "completed",
          input: { file_path: "client/src/app.ts", old_string: "const a = 1;", new_string: "const alpha = 1;" },
        }),
      ]),
    ]);

    const [file] = turn!.files;
    expect(file!.path).toBe("client/src/app.ts");
    expect(file!.additions).toBe(1);
    expect(file!.deletions).toBe(1);
  });

  it("marks a written file new, and counts its content as additions", () => {
    const [turn] = deriveTurns([
      user("u1", "Add the composable"),
      assistant("a1", [
        tool("write", {
          status: "completed",
          input: { filePath: "client/src/composables/use-turns.ts", content: "export function useTurns() {}\n" },
        }),
      ]),
    ]);

    const [file] = turn!.files;
    expect(file!.created).toBe(true);
    expect(file!.additions).toBe(1);
    expect(file!.deletions).toBe(0);
  });

  it("adds up several edits of the same file in one round", () => {
    const [turn] = deriveTurns([
      user("u1", "Two passes"),
      assistant("a1", [
        tool("edit", {
          status: "completed",
          input: { filePath: "a.ts" },
          diffLines: [{ type: "add", content: "one", newLineNumber: 1 }],
        }),
        tool("edit", {
          status: "completed",
          input: { filePath: "a.ts" },
          diffLines: [
            { type: "add", content: "two", newLineNumber: 2 },
            { type: "remove", content: "gone", oldLineNumber: 9 },
          ],
        }),
      ]),
    ]);

    expect(turn!.files).toHaveLength(1);
    expect(turn!.files[0]!.additions).toBe(2);
    expect(turn!.files[0]!.deletions).toBe(1);
    expect(turn!.files[0]!.diff).toHaveLength(3);
    expect(turn!.editCount).toBe(2);
  });

  it("ignores an edit that failed or has not finished", () => {
    const [turn] = deriveTurns([
      user("u1", "Try it"),
      assistant("a1", [
        tool("edit", { status: "error", input: { filePath: "a.ts", newString: "x" } }),
        tool("edit", { status: "running", input: { filePath: "b.ts", newString: "y" } }),
      ]),
    ]);

    expect(turn!.files).toHaveLength(0);
    expect(turn!.readOnly).toBe(true);
    expect(turn!.toolCount).toBe(2);
  });

  it("gives a round that only read things a row that says so", () => {
    const [turn] = deriveTurns([
      user("u1", "Why is the dashboard idle?"),
      assistant("a1", [
        tool("read", { status: "completed", input: { filePath: "a.ts" } }),
        tool("grep", { status: "completed", input: { pattern: "idle" } }),
        tool("bash", { status: "completed", input: { command: "dotnet test", description: "Run tests" } }),
      ]),
    ]);

    expect(turn!.readOnly).toBe(true);
    expect(turn!.toolCount).toBe(3);
    expect(turn!.editCount).toBe(0);
    expect(turn!.commands).toEqual([{ label: "dotnet test", ok: true }]);
  });

  it("names every file of a multi-file patch", () => {
    const patch = [
      "--- a/src/one.ts",
      "+++ b/src/one.ts",
      "@@ -1,1 +1,1 @@",
      "-old",
      "+new",
      "--- /dev/null",
      "+++ b/src/two.ts",
      "@@ -0,0 +1,2 @@",
      "+first",
      "+second",
    ].join("\n");

    const [turn] = deriveTurns([
      user("u1", "Apply the patch"),
      assistant("a1", [tool("apply_patch", { status: "completed", input: { patchText: patch } })]),
    ]);

    expect(turn!.files.map((file) => file.path)).toEqual(["src/one.ts", "src/two.ts"]);
    expect(turn!.files[1]!.created).toBe(true);
    expect(turn!.additions).toBe(3);
    expect(turn!.deletions).toBe(1);
  });
});

describe("cost, timing and totals", () => {
  it("adds up the round's tokens, cost and time", () => {
    const [turn] = deriveTurns([
      user("u1", "Do it", 10_000),
      assistant("a1", [text("part one")], {
        createdAt: 11_000,
        completedAt: 12_000,
        cost: 0.1,
        tokens: { input: 1_000, output: 100, reasoning: 0 },
      }),
      assistant("a2", [text("part two")], {
        createdAt: 12_000,
        completedAt: 25_000,
        cost: 0.09,
        tokens: { input: 2_000, output: 200, reasoning: 0 },
      }),
    ]);

    expect(turn!.cost).toBeCloseTo(0.19);
    expect(turn!.tokensInput).toBe(3_000);
    expect(turn!.tokensOutput).toBe(300);
    expect(turn!.durationMs).toBe(15_000);
    expect(turn!.agent).toBe("build");
  });

  it("totals the session across its rounds", () => {
    const turns = deriveTurns([
      user("u1", "One"),
      assistant("a1", [
        tool("edit", {
          status: "completed",
          input: { filePath: "a.ts" },
          diffLines: [{ type: "add", content: "x", newLineNumber: 1 }],
        }),
      ]),
      user("u2", "Two"),
      assistant("a2", [
        tool("edit", {
          status: "completed",
          input: { filePath: "b.ts" },
          diffLines: [{ type: "remove", content: "y", oldLineNumber: 3 }],
        }),
      ]),
    ]);

    expect(turnTotals(turns)).toEqual({ turns: 2, additions: 1, deletions: 1 });
  });
});

describe("subagents", () => {
  const delegation = (over: Partial<DelegationDto> = {}): DelegationDto => ({
    delegationId: "d1",
    parentToolCallId: "call-task-1",
    childSessionId: "child-1",
    title: "explore",
    status: "completed",
    createdAt: new Date(1_500).toISOString(),
    ...over,
  });

  it("puts a subagent in the round whose tool call started it", () => {
    const turns = deriveTurns(
      [
        user("u1", "First", 1_000),
        assistant("a1", [tool("task", { status: "completed", input: { description: "explore" } }, "call-task-1")], {
          createdAt: 1_100,
        }),
        user("u2", "Second", 5_000),
        assistant("a2", [text("done")], { createdAt: 5_100 }),
      ],
      [delegation()],
    );

    expect(turns[0]!.delegations).toHaveLength(0);
    expect(turns[1]!.delegations.map((sub) => sub.title)).toEqual(["explore"]);
  });

  it("falls back to when the subagent started when its call id is unknown", () => {
    const turns = deriveTurns(
      [
        user("u1", "First", 1_000),
        assistant("a1", [text("ok")], { createdAt: 1_100 }),
        user("u2", "Second", 5_000),
        assistant("a2", [text("ok")], { createdAt: 5_100 }),
      ],
      [delegation({ parentToolCallId: null, createdAt: new Date(5_400).toISOString() })],
    );

    expect(turns[0]!.delegations).toHaveLength(1);
    expect(turns[1]!.delegations).toHaveLength(0);
  });
});

describe("parseUnifiedDiff", () => {
  it("numbers lines from the hunk header and spots a created file", () => {
    const { lines, created, paths } = parseUnifiedDiff(
      ["--- /dev/null", "+++ b/new.ts", "@@ -0,0 +1,2 @@", "+one", "+two"].join("\n"),
    );

    expect(created).toBe(true);
    expect(paths).toEqual(["new.ts"]);
    expect(lines).toEqual([
      { type: "add", content: "one", newLineNumber: 1 },
      { type: "add", content: "two", newLineNumber: 2 },
    ]);
  });

  it("returns nothing for text that is not a diff", () => {
    expect(parseUnifiedDiff("just some output").lines).toHaveLength(0);
  });
});

describe("an empty session", () => {
  it("has no turns", () => {
    expect(deriveTurns([])).toEqual([]);
    expect(turnTotals([])).toEqual({ turns: 0, additions: 0, deletions: 0 });
  });
});
