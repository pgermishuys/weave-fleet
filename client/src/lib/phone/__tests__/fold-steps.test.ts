import { describe, expect, it } from "vitest";
import type { AccumulatedMessage, AccumulatedPart } from "@/lib/client-types";
import { foldMessages, summarizeSteps, stepCategory } from "../fold-steps";

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
