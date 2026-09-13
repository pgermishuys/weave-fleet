import { describe, expect, it } from "vitest";
import type { SessionListItem } from "@/api/client";
import { formatCompactAge, sessionRowStatus } from "@/lib/session-row-status";

const NOW = Date.UTC(2026, 8, 12, 12, 0, 0);
const MIN = 60_000;

function item(sessionStatus: string, overrides: Partial<SessionListItem> = {}, updated = NOW - 2 * 60 * MIN): SessionListItem {
  return {
    sessionStatus,
    activityStatus: null,
    session: { id: "s1", title: "Session", time: { created: updated - MIN, updated } },
    ...overrides,
  } as unknown as SessionListItem;
}

describe("formatCompactAge", () => {
  it.each([
    [NOW - 20_000, "now"],
    [NOW - 4 * MIN, "4m"],
    [NOW - 2 * 60 * MIN, "2h"],
    [NOW - 3 * 24 * 60 * MIN, "3d"],
    [NOW - 15 * 24 * 60 * MIN, "2w"],
  ])("formats %s as %s", (ts, expected) => {
    expect(formatCompactAge(ts, NOW)).toBe(expected);
  });

  it("accepts numeric strings and ISO dates", () => {
    expect(formatCompactAge(String(NOW - 5 * MIN), NOW)).toBe("5m");
    expect(formatCompactAge(new Date(NOW - 3 * 60 * MIN).toISOString(), NOW)).toBe("3h");
  });

  it("returns an empty string for unparseable input", () => {
    expect(formatCompactAge("", NOW)).toBe("");
  });
});

describe("sessionRowStatus", () => {
  it("asks for attention when the session waits on the user", () => {
    expect(sessionRowStatus(item("waiting_input"), NOW)).toEqual({ label: "Needs input", tone: "attention", description: "Needs input" });
  });

  it("shows no word for working sessions, because the glyph animates", () => {
    expect(sessionRowStatus(item("active", { activityStatus: "busy" }), NOW)).toEqual({ label: "", tone: "working", description: "Working" });
    expect(sessionRowStatus(item("active", { activityStatus: "delegating" }), NOW)).toEqual({ label: "", tone: "working", description: "Delegating" });
  });

  it("names a retry, with the attempt when the harness reported one", () => {
    expect(sessionRowStatus(item("active", { activityStatus: "retry", retryAttempt: 2 }), NOW))
      .toEqual({ label: "Retry 2", tone: "retry", description: "Retrying (attempt 2)" });
    expect(sessionRowStatus(item("active", { activityStatus: "retry" }), NOW))
      .toEqual({ label: "Retrying", tone: "retry", description: "Retrying" });
  });

  it("names lifecycle states quietly", () => {
    expect(sessionRowStatus(item("completed"), NOW).label).toBe("Done");
    expect(sessionRowStatus(item("error"), NOW)).toEqual({ label: "Error", tone: "error", description: "Error" });
  });

  it("shows last activity for idle sessions", () => {
    expect(sessionRowStatus(item("idle"), NOW)).toEqual({ label: "2h", tone: "quiet", description: "Idle" });
  });

  it("shows last activity for stopped sessions, which wake on the next prompt", () => {
    expect(sessionRowStatus(item("stopped"), NOW)).toEqual({ label: "2h", tone: "quiet", description: "Idle" });
  });
});
