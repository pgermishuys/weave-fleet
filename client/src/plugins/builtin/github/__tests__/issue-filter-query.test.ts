import { describe, expect, it } from "vitest";
import { DEFAULT_ISSUE_FILTER } from "@/plugins/builtin/github/composables/github-types";
import { issueFilterQuery } from "@/plugins/builtin/github/lib/issue-filter-query";

describe("issueFilterQuery", () => {
  it("turns the issue filter bar's choices into search qualifiers", () => {
    expect(issueFilterQuery({ ...DEFAULT_ISSUE_FILTER, labels: ["bug", "good first issue"], assignee: "none", author: "pat", search: " flicker " }))
      .toBe('is:open label:bug label:"good first issue" author:pat no:assignee sort:updated-desc flicker');
    expect(issueFilterQuery({ ...DEFAULT_ISSUE_FILTER, state: "all", milestone: "v0.36", sort: "comments", direction: "asc" }))
      .toBe("milestone:v0.36 sort:comments-asc");
  });
});
