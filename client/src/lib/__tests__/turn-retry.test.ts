import { describe, expect, it } from "vitest";
import { limitTitle, toScheduledRetry } from "@/lib/turn-retry";

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
