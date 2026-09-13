import { describe, expect, it } from "vitest";
import { parseProgressDetail, parseProgressSummary, sameProgressSummary } from "@/lib/session-progress";

describe("session progress", () => {
  it("reads a row summary", () => {
    expect(parseProgressSummary({ sessionId: "s1", kind: "todos", done: 1, total: 3, current: "Next" }))
      .toEqual({ sessionId: "s1", kind: "todos", done: 1, total: 3, current: "Next" });
    expect(parseProgressSummary({ sessionId: "s1", kind: "todos", done: 0, total: 0 }))
      .toEqual({ sessionId: "s1", kind: "todos", done: 0, total: 0, current: null });
  });

  it.each([
    null,
    "nope",
    { kind: "todos", done: 1, total: 3 },
    { sessionId: "", kind: "todos", done: 1, total: 3 },
    { sessionId: "s1", done: 1, total: 3 },
    { sessionId: "s1", kind: "todos", done: -1, total: 3 },
    { sessionId: "s1", kind: "todos", done: 1.5, total: 3 },
  ])("rejects a malformed summary: %j", (value) => {
    expect(parseProgressSummary(value)).toBeNull();
  });

  it("reads the detail, fills in missing priorities and drops todos without text", () => {
    const detail = parseProgressDetail({
      sessionId: "s1",
      kind: "todos",
      done: 1,
      total: 2,
      current: "Drop the indexes",
      todos: [
        { content: "Write the migration", status: "completed", priority: "high" },
        { content: "Drop the indexes", status: "in_progress", priority: null },
        { content: "", status: "pending" },
        { status: "pending" },
      ],
      updatedAt: "2026-09-13T12:24:00.0000000Z",
    });

    expect(detail?.todos).toEqual([
      { content: "Write the migration", status: "completed", priority: "high" },
      { content: "Drop the indexes", status: "in_progress", priority: "medium" },
    ]);
    expect(detail?.updatedAt).toBe("2026-09-13T12:24:00.0000000Z");
  });

  it("rejects a detail without an update time", () => {
    expect(parseProgressDetail({ sessionId: "s1", kind: "todos", done: 0, total: 1, todos: [] })).toBeNull();
  });

  it("compares summaries by what the row draws", () => {
    const summary = { sessionId: "s1", kind: "todos", done: 1, total: 3, current: "Next" };

    expect(sameProgressSummary(summary, { ...summary })).toBe(true);
    expect(sameProgressSummary(summary, { ...summary, done: 2 })).toBe(false);
    expect(sameProgressSummary(summary, { ...summary, current: null })).toBe(false);
    expect(sameProgressSummary({ ...summary, current: undefined }, { ...summary, current: null })).toBe(true);
    expect(sameProgressSummary(null, undefined)).toBe(true);
    expect(sameProgressSummary(summary, null)).toBe(false);
  });
});
