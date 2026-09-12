import { describe, expect, it } from "vitest";
import {
  formatCheckFailurePrompt,
  formatCheckedAgo,
  isHeaderLink,
  isVisibleLink,
  linkNumber,
  linkTitle,
  needsAttention,
  parseWireLink,
  summarizeChecks,
  type CheckRun,
  type SmartLink,
  type SmartLinkWire,
} from "@/lib/smart-links";

function wire(overrides: Partial<SmartLinkWire> = {}): SmartLinkWire {
  return {
    id: "l1",
    sessionId: "s1",
    url: "https://github.com/owner/repo/pull/187",
    providerId: "github",
    resourceType: "pull_request",
    resourceId: "owner/repo#187",
    title: "owner/repo #187: feat(client): mock API",
    status: "open",
    statusLabel: "Open",
    metadataJson: null,
    isDismissed: false,
    isTerminal: false,
    createdAt: "2026-09-12T00:00:00Z",
    updatedAt: "2026-09-12T00:00:00Z",
    relationship: "own",
    enrichmentStatus: "resolved",
    lastCheckedAt: null,
    ...overrides,
  };
}

function run(name: string, status: string, conclusion: string | null): CheckRun {
  return { id: name.length, name, status, conclusion, htmlUrl: `https://ci/${name}`, workflowName: "CI", startedAt: null, completedAt: null };
}

function withChecks(runs: CheckRun[], extra: Record<string, unknown> = {}): SmartLink {
  return parseWireLink(wire({ metadataJson: JSON.stringify({ owner: "owner", repo: "repo", number: 187, ci: { headSha: "abc1234def", ciStatus: "failure", checkRuns: runs }, ...extra }) }));
}

describe("smart-links", () => {
  it("parses wire links and defaults unknown relationship and status", () => {
    const link = parseWireLink(wire({ relationship: "weird", enrichmentStatus: undefined, metadataJson: "{not json" }));

    expect(link.relationship).toBe("mentioned");
    expect(link.enrichmentStatus).toBe("resolved");
    expect(link.metadata).toEqual({});
  });

  it("strips the owner/repo prefix from titles and falls back to a number", () => {
    expect(linkTitle(parseWireLink(wire()))).toBe("feat(client): mock API");
    expect(linkTitle(parseWireLink(wire({ title: "owner/repo#187" })))).toBe("Pull request #187");
    expect(linkNumber(parseWireLink(wire()))).toBe(187);
  });

  it("summarizes checks in words", () => {
    expect(summarizeChecks(withChecks([run("client-tests", "completed", "failure"), run("e2e", "in_progress", null), run("build", "completed", "success")])).text)
      .toBe("1 failing · 1 running · 1 passed");
    expect(summarizeChecks(withChecks([run("a", "completed", "success"), run("b", "completed", "skipped")])).text)
      .toBe("All 2 passed");
    expect(summarizeChecks(parseWireLink(wire())).state).toBe("none");
  });

  it("needs attention for failing checks or conflicts on open pull requests only", () => {
    expect(needsAttention(withChecks([run("a", "completed", "failure")]))).toBe(true);
    expect(needsAttention(withChecks([run("a", "completed", "success")], { mergeable: false }))).toBe(true);
    expect(needsAttention(withChecks([run("a", "completed", "success")], { mergeable: null }))).toBe(false);

    const merged = { ...withChecks([run("a", "completed", "failure")]), isTerminal: true, status: "merged" };
    expect(needsAttention(merged)).toBe(false);
  });

  it("puts origin, own and pinned links in the header and hides unknown mentions", () => {
    expect(isHeaderLink(parseWireLink(wire({ relationship: "origin" })))).toBe(true);
    expect(isHeaderLink(parseWireLink(wire({ relationship: "mentioned" })))).toBe(false);

    expect(isVisibleLink(parseWireLink(wire({ relationship: "mentioned", enrichmentStatus: "not_found" })))).toBe(false);
    expect(isVisibleLink(parseWireLink(wire({ relationship: "pinned", enrichmentStatus: "not_found" })))).toBe(true);
    expect(isVisibleLink(parseWireLink(wire({ isDismissed: true })))).toBe(false);
  });

  it("includes captured failure logs when sending a check to the agent", () => {
    const failing = run("client-tests", "completed", "failure");
    const link = withChecks([failing], {
      ciFailures: [{ sha: "abc1234def", checkRunName: "client-tests", checkRunId: 1, conclusion: "failure", htmlUrl: "https://ci/job", logContent: "Expected 2, got 3", detectedAt: "" }],
    });

    const prompt = formatCheckFailurePrompt(link, failing);

    expect(prompt).toContain("[CI Failure — owner/repo PR #187]");
    expect(prompt).toContain("Commit: abc1234");
    expect(prompt).toContain("Expected 2, got 3");
    expect(prompt).toContain("BEGIN UNTRUSTED CONTENT");
  });

  it("formats how long ago GitHub was checked", () => {
    const now = Date.parse("2026-09-12T12:00:00Z");
    expect(formatCheckedAgo("2026-09-12T11:59:58Z", now)).toBe("just now");
    expect(formatCheckedAgo("2026-09-12T11:59:48Z", now)).toBe("12s ago");
    expect(formatCheckedAgo("2026-09-12T11:56:00Z", now)).toBe("4m ago");
    expect(formatCheckedAgo(null, now)).toBeNull();
  });
});
