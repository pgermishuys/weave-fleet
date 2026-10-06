import { describe, expect, it } from "vitest";
import type { AccumulatedMessage, AccumulatedPart } from "@/lib/client-types";
import { foldMessages, groupTools, stepCategory, stepDetail, stepRow, summarizeSteps, visibleSteps, type FoldedStep } from "../fold-steps";

function tool(id: string, name: string, status = "completed", input: Record<string, unknown> = {}, metadata?: Record<string, unknown>): AccumulatedPart {
  return { partId: id, type: "tool", tool: name, callId: id, state: { status, input, ...(metadata ? { metadata } : {}) } };
}

function text(id: string, value: string): AccumulatedPart {
  return { partId: id, type: "text", text: value };
}

function message(id: string, role: AccumulatedMessage["role"], parts: AccumulatedPart[], createdAt = 1): AccumulatedMessage {
  return { messageId: id, sessionId: "s", role, parts, createdAt };
}

describe("foldMessages", () => {
  it("folds a run of tool calls into one row and keeps the agent's words", () => {
    const blocks = foldMessages([
      message("u1", "user", [text("u1t", "The reconnect test fails about 1 in 10 runs on CI.")]),
      message("a1", "assistant", [
        tool("r1", "read", "completed", { filePath: "a.cs" }),
        tool("r2", "read"),
        tool("g1", "grep"),
        tool("e1", "edit"),
        text("a1t", "CI schedules the first reconnect retry at 2 s."),
        tool("b1", "bash", "running", { command: "dotnet test" }),
      ]),
    ]);

    expect(blocks.map((b) => b.kind)).toEqual(["user", "steps", "text", "steps"]);
    const first = blocks[1];
    expect(first.kind === "steps" && first.summary).toBe("Read 2 files · edited 1 · searched 1");
    const last = blocks[3];
    expect(last.kind === "steps" && last.running).toBe(true);
    expect(last.kind === "steps" && last.steps[0].label).toBe("dotnet test");
  });

  it("folds across the messages of one turn until the agent says something", () => {
    const blocks = foldMessages([
      message("a1", "assistant", [tool("r1", "read")]),
      message("a2", "assistant", [tool("b1", "bash"), tool("x1", "webfetch")]),
      message("a3", "assistant", [text("t", "Done.")]),
    ]);

    expect(blocks.map((b) => b.kind)).toEqual(["steps", "text"]);
    expect(blocks[0].kind === "steps" && blocks[0].steps).toHaveLength(3);
  });

  it("keeps a subagent call as a row whatever the case of its tool's name", () => {
    const blocks = foldMessages([
      message("a1", "assistant", [tool("t1", "Task", "running", { description: "Review the scripts" }, { sessionId: "child-1" })]),
    ]);

    expect(blocks).toEqual([expect.objectContaining({ kind: "subagent", title: "Review the scripts", childSessionId: "child-1" })]);
  });

  it("keeps subagents as rows and turns questions into lines", () => {
    const blocks = foldMessages([
      message("a1", "assistant", [
        tool("r1", "read"),
        tool("t1", "task", "running", { description: "Look for other tests with the same race", subagent_type: "shuttle" }, { sessionId: "child-1" }),
        tool("q1", "question", "completed", { questions: [{ header: "401", question: "Plain 401 or a page?", options: [] }] }, { answers: [["Plain 401"]] }),
        tool("q2", "question", "running", { questions: [{ header: "x", question: "Which?", options: [] }] }),
      ]),
    ]);

    expect(blocks.map((b) => b.kind)).toEqual(["steps", "subagent", "question", "question"]);
    expect(blocks[1]).toMatchObject({ title: "Look for other tests with the same race", agent: "shuttle", running: true, childSessionId: "child-1" });
    expect(blocks[2]).toMatchObject({ question: "Plain 401 or a page?", answer: "Plain 401", pending: false });
    expect(blocks[3]).toMatchObject({ pending: true, answer: null });
  });

  it("shows shell commands, failures and counts failed steps", () => {
    const blocks = foldMessages([
      message("sh1", "shell", [tool("c1", "shell", "completed", { command: "git status" })]),
      { ...message("a1", "assistant", [tool("b1", "bash", "error")]), turnError: { name: "x", message: "The model is overloaded.", isRetryable: false } },
    ]);

    expect(blocks.map((b) => b.kind)).toEqual(["shell", "steps", "error"]);
    expect(blocks[1].kind === "steps" && blocks[1].failed).toBe(1);
    expect(blocks[2]).toMatchObject({ text: "The model is overloaded." });
  });

  it("drops empty text and empty user messages", () => {
    expect(foldMessages([message("u", "user", [text("e", "  ")]), message("a", "assistant", [text("e2", "")])])).toEqual([]);
  });
});

describe("summaries", () => {
  it("names each kind of step", () => {
    expect(stepCategory("Glob")).toBe("search");
    expect(summarizeSteps([])).toBe("Worked");
    const steps = foldMessages([message("a", "assistant", [tool("1", "bash"), tool("2", "todowrite")])])[0];
    expect(steps.kind === "steps" && steps.summary).toBe("Ran 1 command · used 1 tool");
  });
});

describe("tool rows", () => {
  const steps = (parts: AccumulatedPart[]): FoldedStep[] => {
    const block = foldMessages([message("a", "assistant", parts)])[0];
    return block.kind === "steps" ? block.steps : [];
  };

  it("draws each step as the desktop's tool row: label, detail, and how it ended", () => {
    const [read, grep, edit, bash, failed, running] = steps([
      tool("r", "read", "completed", { filePath: "tests/WeaveFleet.E2E/SignalRTransportTests.cs" }),
      tool("g", "grep", "completed", { pattern: "WaitForReconnect(" }),
      tool("e", "edit", "completed", { filePath: "tests/TestHarness.cs", oldString: "a\nwait 2", newString: "a\nwait 5" }),
      tool("b", "bash", "completed", { command: "bun run test", description: "Run the tests" }),
      tool("x", "bash", "error", { command: "dotnet build" }),
      tool("y", "shell", "running", { command: "dotnet ef migrations add X" }),
    ]);
    expect(stepRow(read)).toEqual({ label: "Read", detail: "tests/WeaveFleet.E2E/SignalRTransportTests.cs", pattern: false, result: "done", adds: 0, dels: 0 });
    expect(stepRow(grep)).toMatchObject({ label: "Grep", detail: "WaitForReconnect(", pattern: true, result: "done" });
    expect(stepRow(edit)).toMatchObject({ label: "Edit", detail: "tests/TestHarness.cs", result: "diff", adds: 1, dels: 1 });
    expect(stepRow(bash)).toMatchObject({ label: "Bash", detail: "bun run test", result: "done" });
    expect(stepRow(failed).result).toBe("failed");
    expect(stepRow(running)).toMatchObject({ label: "Shell", detail: "dotnet ef migrations add X", result: "running" });
  });

  it("counts a unified diff the harness attached", () => {
    const [edit] = steps([tool("e", "edit", "completed", { filePath: "a.ts" }, { diff: "--- a/a.ts\n+++ b/a.ts\n@@ -1 +1,2 @@\n-old\n+new\n+more" })]);
    expect(stepRow(edit)).toMatchObject({ result: "diff", adds: 2, dels: 1 });
  });

  it("shows the first three steps and folds the rest, never leaving just one behind", () => {
    expect(visibleSteps([1, 2, 3])).toEqual({ rows: [1, 2, 3], more: [] });
    expect(visibleSteps([1, 2, 3, 4])).toEqual({ rows: [1, 2, 3, 4], more: [] });
    expect(visibleSteps([1, 2, 3, 4, 5, 6])).toEqual({ rows: [1, 2, 3], more: [4, 5, 6] });
  });
});

describe("a step opened", () => {
  const only = (part: AccumulatedPart): FoldedStep => {
    const block = foldMessages([message("a", "assistant", [part])])[0];
    if (block.kind !== "steps") throw new Error("not steps");
    return block.steps[0];
  };

  it("shows an edit as its diff, with the file and its counts", () => {
    const detail = stepDetail(only(tool("e", "edit", "completed", { filePath: "tests/TestHarness.cs", oldString: "var wait = 2;", newString: "var wait = 5;" })));
    expect(detail).toEqual({ kind: "diff", file: "TestHarness.cs", adds: 1, dels: 1, lines: [{ kind: "del", text: "- var wait = 2;" }, { kind: "add", text: "+ var wait = 5;" }] });
  });

  it("keeps a unified diff's hunk headers", () => {
    const detail = stepDetail(only(tool("e", "edit", "completed", { filePath: "a.ts" }, { diff: "--- a/a.ts\n+++ b/a.ts\n@@ -1 +1 @@\n-old\n+new" })));
    expect(detail.kind === "diff" && detail.lines.map((l) => l.kind)).toEqual(["hunk", "del", "add"]);
  });

  it("shows a command with what it printed, or says it's still running", () => {
    expect(stepDetail(only(tool("b", "bash", "completed", { command: "bun run test" }, undefined)))).toEqual({ kind: "output", command: "bun run test", text: "No output." });
    const done = only({ partId: "b", type: "tool", tool: "bash", callId: "b", state: { status: "completed", input: { command: "ls" }, output: "a\nb\n" } } as AccumulatedPart);
    expect(stepDetail(done)).toEqual({ kind: "output", command: "ls", text: "a\nb" });
    expect(stepDetail(only(tool("r", "read", "running", { filePath: "a.cs" })))).toEqual({ kind: "output", command: null, text: "Still running…" });
  });
});

describe("tool boxes", () => {
  it("puts tool runs and subagents that follow each other in one box", () => {
    const items = groupTools(foldMessages([
      message("u", "user", [text("ut", "Fix it")]),
      message("a", "assistant", [
        tool("r", "read"),
        text("t", "Found it."),
        tool("e", "edit"),
        tool("s", "task", "running", { description: "Look for the same race", subagent_type: "shuttle" }),
        tool("b", "bash", "running", { command: "dotnet test" }),
      ]),
    ]));
    expect(items.map((item) => item.kind)).toEqual(["user", "tools", "text", "tools"]);
    const last = items[3];
    expect(last.kind === "tools" && last.parts.map((part) => part.kind)).toEqual(["steps", "subagent", "steps"]);
  });
});
