import { describe, expect, it } from "vitest";
import {
  codeSpans,
  currentPlanStep,
  parseProgressDetail,
  parseProgressSummary,
  plainText,
  sameProgressSummary,
} from "@/lib/session-progress";

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

describe("session plan", () => {
  const planWire = {
    path: ".weave/plans/p.md",
    title: "P",
    trackedSince: "2026-09-13T11:00:00Z",
    groups: [
      { title: "Phase 1", steps: [{ key: "1", number: "1", title: "One", checked: true, subDone: 1, subTotal: 2, tickedAt: "2026-09-13T11:05:00Z", tickedInMessageId: "m1" }] },
      { title: "Empty", steps: [] },
      { title: null, steps: [{ key: "Two", number: null, title: "Two", checked: false }, { title: "no key" }] },
    ],
  };

  it("reads the plan in a detail and drops empty groups and malformed steps", () => {
    const detail = parseProgressDetail({ sessionId: "s1", kind: "plan", done: 1, total: 2, todos: [], updatedAt: "x", plan: planWire });

    expect(detail?.plan?.groups.map((group) => group.title)).toEqual(["Phase 1", null]);
    expect(detail?.plan?.groups[0]?.steps[0]).toEqual({
      key: "1", number: "1", title: "One", checked: true, subDone: 1, subTotal: 2, tickedAt: "2026-09-13T11:05:00Z", tickedInMessageId: "m1",
    });
    expect(detail?.plan?.groups[1]?.steps).toEqual([
      { key: "Two", number: null, title: "Two", checked: false, subDone: 0, subTotal: 0, tickedAt: null, tickedInMessageId: null },
    ]);
  });

  it("has no plan when the detail carries none", () => {
    expect(parseProgressDetail({ sessionId: "s1", kind: "todos", done: 0, total: 1, todos: [], updatedAt: "x" })?.plan).toBeNull();
    expect(parseProgressDetail({ sessionId: "s1", kind: "todos", done: 0, total: 1, todos: [], updatedAt: "x", plan: { path: "p.md", groups: [] } })?.plan).toBeNull();
  });

  it("finds the current step: the first unticked one", () => {
    const plan = parseProgressDetail({ sessionId: "s1", kind: "plan", done: 1, total: 2, todos: [], updatedAt: "x", plan: planWire })!.plan!;

    expect(currentPlanStep(plan)?.step.key).toBe("Two");
    expect(currentPlanStep({ ...plan, groups: [plan.groups[0]!] })).toBeNull();
  });

  it("splits code spans and strips backticks", () => {
    expect(codeSpans("Map `SessionSnapshot` to `Fleet`")).toEqual([
      { text: "Map ", code: false },
      { text: "SessionSnapshot", code: true },
      { text: " to ", code: false },
      { text: "Fleet", code: true },
    ]);
    expect(codeSpans("``")).toEqual([{ text: "``", code: false }]);
    expect(plainText("Remove `ApplyStreamingDeltas` from `SessionEventsHub`")).toBe("Remove ApplyStreamingDeltas from SessionEventsHub");
  });
});
