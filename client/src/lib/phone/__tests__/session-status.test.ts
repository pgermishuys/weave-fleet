import { describe, expect, it } from "vitest";
import { headerStatus, type HeaderStatusInput } from "../session-status";

const NOW = Date.parse("2026-10-05T14:07:00");
const base: HeaderStatusInput = {
  sessionStatus: "active",
  streamStatus: "busy",
  turnStartedAt: NOW - 72_000,
  updatedAt: NOW - 120_000,
  lastMessageAt: Date.parse("2026-10-05T14:04:00"),
  hasMessages: true,
  unreachableSince: null,
  now: NOW,
};

describe("headerStatus", () => {
  it("says working and for how long", () => {
    expect(headerStatus(base)).toEqual({ tone: "working", state: "Working", detail: "1m 12s" });
    expect(headerStatus({ ...base, streamStatus: "retry" }).state).toBe("Retrying");
  });

  it("says needs you, in amber", () => {
    expect(headerStatus({ ...base, streamStatus: "waiting_input" })).toEqual({ tone: "needs-you", state: "Needs you", detail: "2m 0s" });
    expect(headerStatus({ ...base, streamStatus: "idle", sessionStatus: "waiting_input" }).tone).toBe("needs-you");
  });

  it("says finished with the time", () => {
    const status = headerStatus({ ...base, streamStatus: "idle", sessionStatus: "idle" });
    expect(status.tone).toBe("finished");
    expect(status.detail).toMatch(/04/);
  });

  it("says when the machine was last heard", () => {
    expect(headerStatus({ ...base, unreachableSince: Date.parse("2026-10-05T14:29:00") }).tone).toBe("unreachable");
  });

  it("says errors and brand-new sessions", () => {
    expect(headerStatus({ ...base, streamStatus: "idle", sessionStatus: "error" }).tone).toBe("error");
    expect(headerStatus({ ...base, streamStatus: "idle", sessionStatus: "idle", hasMessages: false }).tone).toBe("idle");
  });
});
