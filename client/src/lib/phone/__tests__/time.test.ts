import { describe, expect, it } from "vitest";
import { ago, duration } from "../time";

const NOW = 1_000_000_000_000;

describe("time words", () => {
  it("says how long ago", () => {
    expect(ago(NOW - 10_000, NOW)).toBe("just now");
    expect(ago(NOW - 3 * 60_000, NOW)).toBe("3 min ago");
    expect(ago(NOW - 2 * 3_600_000, NOW)).toBe("2 h ago");
    expect(ago(NOW - 30 * 3_600_000, NOW)).toBe("yesterday");
    expect(ago(NOW - 3 * 86_400_000, NOW)).toBe("3 days ago");
    expect(ago(null, NOW)).toBe("");
  });

  it("says how long it's been going", () => {
    expect(duration(NOW - 42_000, NOW)).toBe("42s");
    expect(duration(NOW - 72_000, NOW)).toBe("1m 12s");
    expect(duration(NOW - 6 * 60_000 - 40_000, NOW)).toBe("6m 40s");
    expect(duration(NOW - 22 * 60_000, NOW)).toBe("22m");
    expect(duration(NOW - 125 * 60_000, NOW)).toBe("2h 5m");
  });
});
