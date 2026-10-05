import { beforeEach, describe, expect, it } from "vitest";
import type { PhoneBlock } from "../fold-steps";
import { lastSeenAt, markSeen, sinceYouLookedIndex } from "../last-seen";

const block = (key: string, createdAt?: number): PhoneBlock => ({ kind: "text", key, messageId: key, text: key, createdAt });

describe("last seen", () => {
  beforeEach(() => localStorage.clear());

  it("remembers when you looked, per machine and session", () => {
    markSeen("hangar", "s1", 1000);
    expect(lastSeenAt("hangar", "s1")).toBe(1000);
    expect(lastSeenAt("falcon", "s1")).toBeNull();
  });

  it("puts the line before the first newer block", () => {
    const blocks = [block("a", 100), block("b", 200), block("c", 300)];
    expect(sinceYouLookedIndex(blocks, 150)).toBe(1);
    expect(sinceYouLookedIndex(blocks, 300)).toBeNull();
    expect(sinceYouLookedIndex(blocks, null)).toBeNull();
  });

  it("puts no line when everything is new", () => {
    expect(sinceYouLookedIndex([block("a", 100)], 50)).toBeNull();
  });

  it("skips blocks without a time", () => {
    expect(sinceYouLookedIndex([block("a", 100), block("b"), block("c", 300)], 150)).toBe(2);
  });
});
