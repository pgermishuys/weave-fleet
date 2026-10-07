import { describe, expect, it } from "vitest";
import { describeRetry, retryCountdown, retryShortLabel } from "@/lib/retry-status";

const now = Date.parse("2026-10-07T10:00:00Z");

describe("retry status", () => {
  it("says the attempt out of how many, when and why", () => {
    expect(describeRetry({ attempt: 3, maxAttempts: 10, message: "API overloaded (529)", next: "2026-10-07T10:00:12Z" }, now))
      .toBe("Retrying · attempt 3 of 10 · in 12 s · API overloaded (529)");
  });

  it("leaves out what the harness didn't say (OpenCode 2 gives no maximum)", () => {
    expect(describeRetry({ attempt: 2, message: "Too many requests" }, now)).toBe("Retrying · attempt 2 · Too many requests");
    expect(describeRetry(null, now)).toBe("Retrying");
  });

  it("counts down in seconds, then minutes, then says now", () => {
    expect(retryCountdown({ next: "2026-10-07T10:00:00.400Z" }, now)).toBe("in 1 s");
    expect(retryCountdown({ next: "2026-10-07T10:02:30Z" }, now)).toBe("in 3 min");
    expect(retryCountdown({ next: "2026-10-07T09:59:59Z" }, now)).toBe("now");
    expect(retryCountdown({ next: "not a time" }, now)).toBeNull();
  });

  it("is short on a session row", () => {
    expect(retryShortLabel({ attempt: 3, maxAttempts: 10 })).toBe("Retry 3/10");
    expect(retryShortLabel({ attempt: 3 })).toBe("Retry 3");
    expect(retryShortLabel({})).toBe("Retrying");
  });
});
