import { describe, it, expect } from "vitest";
import { formatContextAsPrompt } from "../context-formatter";
import type { ContextSource } from "@/integrations/types";

describe("formatContextAsPrompt", () => {
  const baseIssue: ContextSource = {
    type: "github-issue",
    url: "https://github.com/acme/repo/issues/42",
    title: "Issue #42: Add dark mode",
    body: "We need dark mode support.",
    metadata: {
      owner: "acme",
      repo: "repo",
      number: 42,
      labels: [{ name: "enhancement", color: "a2eeef" }],
      state: "open",
      comments: [],
    },
  };

  const basePr: ContextSource = {
    type: "github-pr",
    url: "https://github.com/acme/repo/pull/10",
    title: "PR #10: feat: streaming",
    body: "Adds streaming response handler.",
    metadata: {
      owner: "acme",
      repo: "repo",
      number: 10,
      labels: [{ name: "feature", color: "a2eeef" }],
      state: "open",
      additions: 342,
      deletions: 28,
      changed_files: 5,
      head: "feat/streaming",
      base: "main",
      draft: false,
      comments: [],
    },
  };

  describe("GitHub issue formatting", () => {
    it("ShouldIncludeIssueTitleAndUrl", () => {
      const result = formatContextAsPrompt(baseIssue);
      expect(result).toContain("# Context: GitHub Issue");
      expect(result).toContain("Issue #42: Add dark mode");
      expect(result).toContain("https://github.com/acme/repo/issues/42");
    });

    it("ShouldIncludeRepositoryAndState", () => {
      const result = formatContextAsPrompt(baseIssue);
      expect(result).toContain("acme/repo");
      expect(result).toContain("open");
    });

    it("ShouldIncludeLabels", () => {
      const result = formatContextAsPrompt(baseIssue);
      expect(result).toContain("enhancement");
    });

    it("ShouldIncludeBodyContent", () => {
      const result = formatContextAsPrompt(baseIssue);
      expect(result).toContain("We need dark mode support.");
    });

    it("ShouldIncludeActionPrompt", () => {
      const result = formatContextAsPrompt(baseIssue);
      expect(result).toContain("This issue has been loaded as context. What would you like to do?");
    });

    it("ShouldHandleEmptyBody", () => {
      const issue: ContextSource = { ...baseIssue, body: "" };
      const result = formatContextAsPrompt(issue);
      expect(result).toContain("_No description provided._");
    });

    it("ShouldHandleNoLabels", () => {
      const issue: ContextSource = {
        ...baseIssue,
        metadata: { ...baseIssue.metadata, labels: [] },
      };
      const result = formatContextAsPrompt(issue);
      expect(result).toContain("**Labels**: None");
    });

    it("ShouldIncludeComments", () => {
      const issue: ContextSource = {
        ...baseIssue,
        metadata: {
          ...baseIssue.metadata,
          comments: [
            {
              author: "alice",
              body: "This is critical!",
              createdAt: new Date(Date.now() - 3600000).toISOString(),
            },
          ],
        },
      };
      const result = formatContextAsPrompt(issue);
      expect(result).toContain("## Comments");
      expect(result).toContain("@alice");
      expect(result).toContain("This is critical!");
    });

    it("ShouldNotIncludeCommentsSection_WhenNoComments", () => {
      const result = formatContextAsPrompt(baseIssue);
      expect(result).not.toContain("## Comments");
    });
  });

  describe("GitHub PR formatting", () => {
    it("ShouldIncludePrTitleAndUrl", () => {
      const result = formatContextAsPrompt(basePr);
      expect(result).toContain("# Context: GitHub Pull Request");
      expect(result).toContain("PR #10: feat: streaming");
      expect(result).toContain("https://github.com/acme/repo/pull/10");
    });

    it("ShouldIncludeBranchInfo", () => {
      const result = formatContextAsPrompt(basePr);
      expect(result).toContain("`feat/streaming` → `main`");
    });

    it("ShouldIncludeDiffStats", () => {
      const result = formatContextAsPrompt(basePr);
      expect(result).toContain("+342");
      expect(result).toContain("-28");
      expect(result).toContain("5 files");
    });

    it("ShouldShowDraftState", () => {
      const pr: ContextSource = {
        ...basePr,
        metadata: { ...basePr.metadata, draft: true },
      };
      const result = formatContextAsPrompt(pr);
      expect(result).toContain("draft");
    });

    it("ShouldIncludeActionPrompt", () => {
      const result = formatContextAsPrompt(basePr);
      expect(result).toContain("This pull request has been loaded as context. What would you like to do?");
    });

    it("ShouldHandleEmptyBody", () => {
      const pr: ContextSource = { ...basePr, body: "" };
      const result = formatContextAsPrompt(pr);
      expect(result).toContain("_No description provided._");
    });

    it("ShouldHandleMergedState", () => {
      const pr: ContextSource = {
        ...basePr,
        metadata: { ...basePr.metadata, state: "merged", draft: false },
      };
      const result = formatContextAsPrompt(pr);
      expect(result).toContain("merged");
    });
  });

  describe("Generic/unknown type fallback", () => {
    it("ShouldUseGenericFallback_ForUnknownType", () => {
      const unknown: ContextSource = {
        type: "unknown-type",
        url: "https://example.com/item/1",
        title: "Some Item",
        body: "Some content here.",
        metadata: { key: "value" },
      };
      const result = formatContextAsPrompt(unknown);
      expect(result).toContain("# Context: Some Item");
      expect(result).toContain("unknown-type");
      expect(result).toContain("Some content here.");
      expect(result).toContain("This context has been loaded. What would you like to do?");
    });

    it("ShouldIncludeMetadata_InFallback", () => {
      const unknown: ContextSource = {
        type: "unknown",
        url: "https://example.com",
        title: "Title",
        body: "",
        metadata: { foo: "bar", count: 42 },
      };
      const result = formatContextAsPrompt(unknown);
      expect(result).toContain("foo");
      expect(result).toContain("bar");
    });

    it("ShouldHandleSpecialCharacters", () => {
      const issue: ContextSource = {
        ...baseIssue,
        title: "Issue #99: Fix <script> injection & `code`",
        body: 'Malicious body: <img src="x" onerror="alert(1)">',
      };
      const result = formatContextAsPrompt(issue);
      expect(result).toContain("Issue #99");
      expect(result).toContain("Malicious body");
    });
  });
});
