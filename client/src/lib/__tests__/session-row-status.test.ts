import { describe, expect, it } from "vitest";
import type { SessionListItem } from "@/api/client";
import { formatCompactAge, isSessionLive, sessionRowDim, sessionRowStatus } from "@/lib/session-row-status";

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
      .toEqual({ label: "Retry 2", tone: "retry", description: "Retrying · attempt 2" });
    expect(sessionRowStatus(item("active", { activityStatus: "retry" }), NOW))
      .toEqual({ label: "Retrying", tone: "retry", description: "Retrying" });
  });

  // Claude Code says how many attempts it makes, when, and why.
  it("names a retry out of how many, and says when and why", () => {
    expect(sessionRowStatus(item("active", {
      activityStatus: "retry",
      retryAttempt: 3,
      retryMaxAttempts: 10,
      retryMessage: "API overloaded (529)",
      retryNext: new Date(NOW + 12_000).toISOString(),
    }), NOW)).toEqual({ label: "Retry 3/10", tone: "retry", description: "Retrying · attempt 3 of 10 · in 12 s · API overloaded (529)" });
  });

  it("names lifecycle states quietly", () => {
    expect(sessionRowStatus(item("completed"), NOW).label).toBe("Done");
    expect(sessionRowStatus(item("error"), NOW)).toEqual({ label: "Error", tone: "error", description: "Error" });
  });

  it("shows last activity for idle sessions", () => {
    expect(sessionRowStatus(item("idle"), NOW)).toEqual({ label: "2h", tone: "quiet", description: "Idle" });
  });

  it("says when Fleet tries a turn a limit stopped again", () => {
    const dueAt = new Date(NOW + 2 * 60 * MIN + 5 * MIN).toISOString();
    const status = sessionRowStatus(
      item("idle", { scheduledRetry: { dueAt, attempt: 1, kind: "usage_limit", reason: "You've hit your session limit", providerSaid: true } }),
      NOW,
    );

    const clock = new Date(dueAt).toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" });
    expect(status).toEqual({
      label: `Retry ${clock}`,
      tone: "retry",
      description: `Stopped by a usage limit. Fleet tries again at ${clock} (in 2 h 5 min)`,
    });
  });

  it("shows last activity for stopped sessions, which wake on the next prompt", () => {
    expect(sessionRowStatus(item("stopped"), NOW)).toEqual({ label: "2h", tone: "quiet", description: "Idle" });
  });
});

describe("isSessionLive", () => {
  it.each(["active", "waiting_input", "error"])("treats %s as live", (status) => {
    expect(isSessionLive(item(status))).toBe(true);
  });

  it.each(["idle", "stopped", "completed", "disconnected"])("treats %s as quiet", (status) => {
    expect(isSessionLive(item(status))).toBe(false);
  });
});

describe("sessionRowDim", () => {
  const DAY = 24 * 60 * MIN;

  it("keeps a session at full contrast within a day of its last activity", () => {
    expect(sessionRowDim(item("idle", {}, NOW - (DAY - MIN)), NOW)).toBe(0);
  });

  it("dims after a day without activity, and further after three", () => {
    expect(sessionRowDim(item("idle", {}, NOW - DAY), NOW)).toBe(1);
    expect(sessionRowDim(item("idle", {}, NOW - (3 * DAY - MIN)), NOW)).toBe(1);
    expect(sessionRowDim(item("idle", {}, NOW - 3 * DAY), NOW)).toBe(2);
    expect(sessionRowDim(item("completed", {}, NOW - 10 * DAY), NOW)).toBe(2);
  });

  it("never dims live sessions, however old their last activity", () => {
    for (const status of ["active", "waiting_input", "error"]) {
      expect(sessionRowDim(item(status, {}, NOW - 10 * DAY), NOW)).toBe(0);
    }
  });

  it("falls back to the creation time, and stays bright without either", () => {
    const createdOnly = item("idle", { session: { id: "s1", title: "Session", time: { created: NOW - 2 * DAY } } } as Partial<SessionListItem>);
    expect(sessionRowDim(createdOnly, NOW)).toBe(1);

    const noTime = item("idle", { session: { id: "s1", title: "Session" } } as Partial<SessionListItem>);
    expect(sessionRowDim(noTime, NOW)).toBe(0);
  });
});

describe("sessionRowStatus while its machine isn't answering", () => {
  it("says a working session's machine isn't answering, instead of working on", () => {
    const working = { sessionStatus: "active", activityStatus: "busy", session: { id: "s1", title: "Session", time: { created: 1 } } } as unknown as SessionListItem;

    expect(sessionRowStatus(working, 2, false)).toEqual({
      label: "Not answering",
      tone: "quiet",
      description: "Its machine isn't answering. It was working when last heard.",
    });
    expect(sessionRowStatus(working, 2, true).tone).toBe("working");
  });

  it("leaves a session that wasn't working as it was", () => {
    const asking = { sessionStatus: "waiting_input", activityStatus: "waiting_input", session: { id: "s1", title: "Session", time: { created: 1 } } } as unknown as SessionListItem;

    expect(sessionRowStatus(asking, 2, false).label).toBe("Needs input");
  });
});
