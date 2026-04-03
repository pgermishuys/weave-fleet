import { describe, expect, it } from "vitest";
import { DEFAULT_ISSUE_FILTER, type IssueFilterState } from "../../types";
import { parseFilterExpression, serializeFilterExpression } from "../filter-expression";

// ─── Parser tests ─────────────────────────────────────────────────────────────

describe("parseFilterExpression", () => {
  it("empty string returns defaults", () => {
    expect(parseFilterExpression("")).toEqual(DEFAULT_ISSUE_FILTER);
  });

  it("whitespace-only string returns defaults", () => {
    expect(parseFilterExpression("   ")).toEqual(DEFAULT_ISSUE_FILTER);
  });

  it("parses is:open", () => {
    const result = parseFilterExpression("is:open");
    expect(result.state).toBe("open");
  });

  it("parses is:closed", () => {
    const result = parseFilterExpression("is:closed");
    expect(result.state).toBe("closed");
  });

  it("parses is:all", () => {
    const result = parseFilterExpression("is:all");
    expect(result.state).toBe("all");
  });

  it("parses a single label", () => {
    const result = parseFilterExpression("label:bug");
    expect(result.labels).toEqual(["bug"]);
  });

  it("parses multiple labels", () => {
    const result = parseFilterExpression("label:bug label:enhancement");
    expect(result.labels).toEqual(["bug", "enhancement"]);
  });

  it("deduplicates repeated labels", () => {
    const result = parseFilterExpression("label:bug label:bug");
    expect(result.labels).toEqual(["bug"]);
  });

  it("parses quoted label with spaces", () => {
    const result = parseFilterExpression('label:"good first issue"');
    expect(result.labels).toEqual(["good first issue"]);
  });

  it("parses milestone", () => {
    const result = parseFilterExpression("milestone:v1.0");
    expect(result.milestone).toBe("v1.0");
  });

  it("parses quoted milestone with spaces", () => {
    const result = parseFilterExpression('milestone:"Sprint 1"');
    expect(result.milestone).toBe("Sprint 1");
  });

  it("parses assignee", () => {
    const result = parseFilterExpression("assignee:octocat");
    expect(result.assignee).toBe("octocat");
  });

  it("parses assignee:none", () => {
    const result = parseFilterExpression("assignee:none");
    expect(result.assignee).toBe("none");
  });

  it("parses assignee:*", () => {
    const result = parseFilterExpression("assignee:*");
    expect(result.assignee).toBe("*");
  });

  it("parses author", () => {
    const result = parseFilterExpression("author:foo");
    expect(result.author).toBe("foo");
  });

  it("parses type", () => {
    const result = parseFilterExpression("type:bug");
    expect(result.type).toBe("bug");
  });

  it("parses sort:created-desc", () => {
    const result = parseFilterExpression("sort:created-desc");
    expect(result.sort).toBe("created");
    expect(result.direction).toBe("desc");
  });

  it("parses sort:created-asc", () => {
    const result = parseFilterExpression("sort:created-asc");
    expect(result.sort).toBe("created");
    expect(result.direction).toBe("asc");
  });

  it("parses sort:updated-asc", () => {
    const result = parseFilterExpression("sort:updated-asc");
    expect(result.sort).toBe("updated");
    expect(result.direction).toBe("asc");
  });

  it("parses sort:comments-desc", () => {
    const result = parseFilterExpression("sort:comments-desc");
    expect(result.sort).toBe("comments");
    expect(result.direction).toBe("desc");
  });

  it("preserves free-text search", () => {
    const result = parseFilterExpression("hello world");
    expect(result.search).toBe("hello world");
  });

  it("mixes qualifiers and free-text", () => {
    const result = parseFilterExpression("is:open label:bug fix crash");
    expect(result.state).toBe("open");
    expect(result.labels).toEqual(["bug"]);
    expect(result.search).toBe("fix crash");
  });

  it("preserves unknown qualifiers as search text", () => {
    const result = parseFilterExpression("unknown:value");
    expect(result.search).toBe("unknown:value");
  });

  it("handles full complex expression", () => {
    const result = parseFilterExpression(
      'is:closed label:bug label:"good first issue" author:octocat assignee:none milestone:v2 sort:created-asc search text'
    );
    expect(result.state).toBe("closed");
    expect(result.labels).toEqual(["bug", "good first issue"]);
    expect(result.author).toBe("octocat");
    expect(result.assignee).toBe("none");
    expect(result.milestone).toBe("v2");
    expect(result.sort).toBe("created");
    expect(result.direction).toBe("asc");
    expect(result.search).toBe("search text");
  });
});

// ─── Serializer tests ─────────────────────────────────────────────────────────

describe("serializeFilterExpression", () => {
  it("defaults produce empty string", () => {
    expect(serializeFilterExpression(DEFAULT_ISSUE_FILTER)).toBe("");
  });

  it("emits is:closed when state is closed", () => {
    const state = { ...DEFAULT_ISSUE_FILTER, state: "closed" as const };
    expect(serializeFilterExpression(state)).toBe("is:closed");
  });

  it("emits is:all when state is all", () => {
    const state = { ...DEFAULT_ISSUE_FILTER, state: "all" as const };
    expect(serializeFilterExpression(state)).toBe("is:all");
  });

  it("emits single label", () => {
    const state = { ...DEFAULT_ISSUE_FILTER, labels: ["bug"] };
    expect(serializeFilterExpression(state)).toBe("label:bug");
  });

  it("emits multiple labels", () => {
    const state = { ...DEFAULT_ISSUE_FILTER, labels: ["bug", "enhancement"] };
    expect(serializeFilterExpression(state)).toBe("label:bug label:enhancement");
  });

  it("quotes label with spaces", () => {
    const state = { ...DEFAULT_ISSUE_FILTER, labels: ["good first issue"] };
    expect(serializeFilterExpression(state)).toBe('label:"good first issue"');
  });

  it("escapes backslashes and quotes in label values", () => {
    const state = { ...DEFAULT_ISSUE_FILTER, labels: ['path\\to "file"'] };
    expect(serializeFilterExpression(state)).toBe('label:"path\\\\to \\"file\\""');
  });

  it("emits sort when non-default", () => {
    const state = { ...DEFAULT_ISSUE_FILTER, sort: "created" as const, direction: "asc" as const };
    expect(serializeFilterExpression(state)).toContain("sort:created-asc");
  });

  it("does not emit sort when at default (updated-desc)", () => {
    const state = { ...DEFAULT_ISSUE_FILTER, sort: "updated" as const, direction: "desc" as const };
    expect(serializeFilterExpression(state)).not.toContain("sort:");
  });

  it("emits search text at end", () => {
    const state = { ...DEFAULT_ISSUE_FILTER, search: "fix crash" };
    expect(serializeFilterExpression(state)).toBe("fix crash");
  });

  it("emits author", () => {
    const state = { ...DEFAULT_ISSUE_FILTER, author: "octocat" };
    expect(serializeFilterExpression(state)).toBe("author:octocat");
  });

  it("emits assignee", () => {
    const state = { ...DEFAULT_ISSUE_FILTER, assignee: "none" };
    expect(serializeFilterExpression(state)).toBe("assignee:none");
  });

  it("emits milestone", () => {
    const state = { ...DEFAULT_ISSUE_FILTER, milestone: "v1.0" };
    expect(serializeFilterExpression(state)).toBe("milestone:v1.0");
  });

  it("quotes milestone with spaces", () => {
    const state = { ...DEFAULT_ISSUE_FILTER, milestone: "Sprint 1" };
    expect(serializeFilterExpression(state)).toBe('milestone:"Sprint 1"');
  });
});

// ─── Round-trip tests ─────────────────────────────────────────────────────────

describe("round-trip (parse → serialize → parse)", () => {
  const cases: Array<{ label: string; state: IssueFilterState }> = [
    {
      label: "defaults",
      state: DEFAULT_ISSUE_FILTER,
    },
    {
      label: "closed with labels",
      state: {
        ...DEFAULT_ISSUE_FILTER,
        state: "closed",
        labels: ["bug", "good first issue"],
      },
    },
    {
      label: "author + assignee + search",
      state: {
        ...DEFAULT_ISSUE_FILTER,
        author: "octocat",
        assignee: "none",
        search: "fix crash",
      },
    },
    {
      label: "sort:created-asc with milestone",
      state: {
        ...DEFAULT_ISSUE_FILTER,
        sort: "created",
        direction: "asc",
        milestone: "v2",
      },
    },
  ];

  for (const { label, state } of cases) {
    it(`round-trips: ${label}`, () => {
      const expr = serializeFilterExpression(state);
      const reparsed = parseFilterExpression(expr);
      expect(reparsed).toEqual(state);
    });
  }
});
