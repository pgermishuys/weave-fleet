import { describe, expect, it } from "vitest";
import { blockReasons, checksFromRollup, parseReviewers, prState, prWords, type PrFacts } from "@/lib/pr-state";

const facts = (overrides: Partial<PrFacts> = {}): PrFacts => ({
  state: "open",
  draft: false,
  checks: "passing",
  conflict: false,
  review: null,
  unresolvedThreads: 0,
  ...overrides,
});

describe("pr-state", () => {
  it("blocks an open pull request on failing checks, conflicts, requested changes or open threads", () => {
    expect(prState(facts())).toBe("open");
    expect(prState(facts({ checks: "failing" }))).toBe("blocked");
    expect(prState(facts({ conflict: true }))).toBe("blocked");
    expect(prState(facts({ review: "CHANGES_REQUESTED" }))).toBe("blocked");
    expect(prState(facts({ unresolvedThreads: 1 }))).toBe("blocked");
    expect(blockReasons(facts({ checks: "failing", conflict: true, review: "CHANGES_REQUESTED", unresolvedThreads: 2 })))
      .toEqual(["checks", "conflict", "changes", "threads"]);
  });

  it("keeps drafts grey and finished pull requests in their own colour", () => {
    expect(prState(facts({ draft: true, checks: "failing" }))).toBe("draft");
    expect(prState(facts({ state: "merged", checks: "failing" }))).toBe("merged");
    expect(prState(facts({ state: "closed" }))).toBe("closed");
    expect(blockReasons(facts({ state: "merged", checks: "failing" }))).toEqual([]);
  });

  it("says what the pull request is waiting on", () => {
    expect(prWords(facts({ checks: "failing", unresolvedThreads: 2, checkCounts: { passed: 3, failing: 1, pending: 1 } })))
      .toBe("1 failing · 2 threads");
    expect(prWords(facts({ checks: "failing", checkCounts: { passed: 3, failing: 1, pending: 0 } }))).toBe("Checks failing");
    expect(prWords(facts({ checks: "pending", checkCounts: { passed: 3, failing: 0, pending: 2 } }))).toBe("3 of 5 checks done");
    expect(prWords(facts({ checks: "pending" }))).toBe("Checks running");
    expect(prWords(facts({ review: "APPROVED" }))).toBe("Ready to merge");
    expect(prWords(facts({ review: "REVIEW_REQUIRED" }))).toBe("Waiting for review");
    expect(prWords(facts({ review: "CHANGES_REQUESTED" }))).toBe("Changes requested");
    expect(prWords(facts({ conflict: true }))).toBe("Conflicts");
    expect(prWords(facts())).toBe("Checks passed");
    expect(prWords(facts({ checks: "none" }))).toBe("Open");
    expect(prWords(facts({ draft: true }))).toBe("Draft");
    expect(prWords(facts({ state: "merged" }))).toBe("Merged");
  });

  it("reads GitHub's checks rollup and reviewers", () => {
    expect(checksFromRollup("failure")).toBe("failing");
    expect(checksFromRollup("pending")).toBe("pending");
    expect(checksFromRollup("success")).toBe("passing");
    expect(checksFromRollup(null)).toBe("none");
    expect(parseReviewers([{ login: "sarah", avatarUrl: "", state: "CHANGES_REQUESTED" }, { state: "APPROVED" }, "junk"]))
      .toEqual([{ login: "sarah", avatarUrl: null, state: "CHANGES_REQUESTED" }]);
  });
});
