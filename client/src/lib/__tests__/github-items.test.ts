import { describe, expect, it } from "vitest";
import { DEFAULT_ISSUE_FILTER } from "@/plugins/builtin/github/composables/github-types";
import { issueFilterQuery, itemPrFacts, itemRoute, needsYou, summaryFromLink, yourPullRequests, type GitHubItemSummary, type GitHubWork } from "@/lib/github-items";
import { parseWireLink } from "@/lib/smart-links";

function pull(number: number, extra: Partial<GitHubItemSummary> = {}): GitHubItemSummary {
  return {
    kind: "pull", owner: "acme", repo: "rocket", number, title: `PR ${number}`, url: `https://github.com/acme/rocket/pull/${number}`,
    state: "open", isDraft: false, author: "pat", authorAvatarUrl: null, createdAt: "", updatedAt: "", comments: 0, labels: [],
    headRef: "feat/x", baseRef: "main", additions: 1, deletions: 1, checks: "success", reviewDecision: null, mergeable: "MERGEABLE",
    reviewers: [], assignees: [], ...extra,
  };
}

describe("github-items", () => {
  it("reads a row's pull request facts", () => {
    expect(itemPrFacts(pull(1, { checks: "failure", mergeable: "CONFLICTING", reviewDecision: "APPROVED" }))).toMatchObject({
      state: "open", checks: "failing", conflict: true, review: "APPROVED",
    });
    expect(itemPrFacts(pull(1, { state: "merged" })).state).toBe("merged");
  });

  it("lists review requests, then the user's blocked pull requests, as what needs them", () => {
    const work: GitHubWork = {
      login: "pat",
      avatarUrl: null,
      reviewRequested: [pull(1)],
      authored: [pull(2, { checks: "failure" }), pull(3, { reviewDecision: "CHANGES_REQUESTED" }), pull(4)],
      assigned: [],
      repos: [],
    };

    expect(needsYou(work).map(({ item, reason }) => [item.number, reason])).toEqual([
      [1, "Review requested"],
      [2, "Checks failing"],
      [3, "Changes requested"],
    ]);
    expect(yourPullRequests(work).map((item) => item.number)).toEqual([4]);
  });

  it("turns the issue filter bar's choices into search qualifiers", () => {
    expect(issueFilterQuery({ ...DEFAULT_ISSUE_FILTER, labels: ["bug", "good first issue"], assignee: "none", author: "pat", search: " flicker " }))
      .toBe('is:open label:bug label:"good first issue" author:pat no:assignee sort:updated-desc flicker');
    expect(issueFilterQuery({ ...DEFAULT_ISSUE_FILTER, state: "all", milestone: "v0.36", sort: "comments", direction: "asc" }))
      .toBe("milestone:v0.36 sort:comments-asc");
  });

  it("builds a row from a session's link", () => {
    const link = parseWireLink({
      id: "l1", sessionId: "s1", url: "https://github.com/acme/rocket/pull/7", providerId: "github", resourceType: "pull_request",
      resourceId: "acme/rocket#7", title: "acme/rocket #7: Queue", status: "draft", statusLabel: "Draft",
      metadataJson: JSON.stringify({ owner: "acme", repo: "rocket", number: 7, headRef: "feat/q", additions: 3, deletions: 1, mergeable: false, author: "pat" }),
      isDismissed: false, isTerminal: false, createdAt: "", updatedAt: "", relationship: "own", enrichmentStatus: "resolved", lastCheckedAt: null,
    });

    expect(summaryFromLink(link)).toMatchObject({
      kind: "pull", owner: "acme", repo: "rocket", number: 7, title: "Queue", state: "open", isDraft: true,
      headRef: "feat/q", additions: 3, deletions: 1, mergeable: "CONFLICTING", author: "pat", checks: "none",
    });
  });

  it("routes to Fleet's page for the item", () => {
    expect(itemRoute(pull(7))).toBe("/github/acme/rocket/pulls/7");
    expect(itemRoute({ kind: "issue", owner: "acme", repo: "rocket", number: 8 })).toBe("/github/acme/rocket/issues/8");
  });
});
