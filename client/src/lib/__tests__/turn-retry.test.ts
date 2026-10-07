import { describe, expect, it } from "vitest";
import { formatRetryClock, formatRetryIn, limitTitle, toScheduledRetry } from "@/lib/turn-retry";

const NOW = new Date(2026, 9, 7, 1, 51, 43).getTime();
const MIN = 60_000;

describe("formatRetryIn", () => {
  it.each([
    [40_000, "in 40 s"],
    [12 * MIN, "in 12 min"],
    [125 * MIN, "in 2 h 5 min"],
    [120 * MIN, "in 2 h"],
    [3 * 24 * 60 * MIN, "in 3 days"],
    [-1, "now"],
  ])("puts %i ms as %s", (left, expected) => {
    expect(formatRetryIn(NOW + left, NOW)).toBe(expected);
  });
});

describe("formatRetryClock", () => {
  it("gives the time for today, the weekday this week, and the date after", () => {
    const time = (at: number) => new Date(at).toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" });
    const today = NOW + 60 * MIN;
    const thursday = new Date(2026, 9, 8, 14, 5).getTime();
    const later = new Date(2026, 9, 20, 9, 0).getTime();

    expect(formatRetryClock(today, NOW)).toBe(time(today));
    expect(formatRetryClock(thursday, NOW)).toBe(`${new Date(thursday).toLocaleDateString(undefined, { weekday: "short" })} ${time(thursday)}`);
    expect(formatRetryClock(later, NOW)).toBe(`${new Date(later).toLocaleDateString(undefined, { day: "numeric", month: "short" })} ${time(later)}`);
  });
});

describe("limitTitle", () => {
  it("names each limit, and nothing else", () => {
    expect(limitTitle("usage_limit")).toBe("Usage limit reached");
    expect(limitTitle("rate_limit")).toBe("Rate limited");
    expect(limitTitle("overloaded")).toBe("The model's provider is overloaded");
    expect(limitTitle(null)).toBeNull();
    expect(limitTitle("APIError")).toBeNull();
  });
});

describe("toScheduledRetry", () => {
  it("reads the server's shape and refuses anything else", () => {
    expect(toScheduledRetry({ dueAt: "2026-10-07T03:44:14Z", attempt: 2, kind: "rate_limit", reason: "Too many requests", providerSaid: false }))
      .toEqual({ dueAt: "2026-10-07T03:44:14Z", attempt: 2, kind: "rate_limit", reason: "Too many requests", providerSaid: false });
    expect(toScheduledRetry(null)).toBeNull();
    expect(toScheduledRetry({ dueAt: "soon", kind: "rate_limit" })).toBeNull();
    expect(toScheduledRetry({ dueAt: "2026-10-07T03:44:14Z", kind: "billing" })).toBeNull();
  });
});
