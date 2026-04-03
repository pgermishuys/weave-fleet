// @vitest-environment jsdom

import { renderHook, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { DEFAULT_ISSUE_FILTER, type IssueFilterState } from "../../types";

const apiFetchMock = vi.fn();

vi.mock("@/lib/api-client", () => ({
  apiFetch: (...args: unknown[]) => apiFetchMock(...args),
}));

function mockResponse(data: unknown) {
  return { ok: true, json: async () => data };
}

async function loadUseGitHubIssues() {
  const mod = await import("@/integrations/github/hooks/use-github-issues");
  return mod.useGitHubIssues;
}

describe("useGitHubIssues", () => {
  beforeEach(() => {
    vi.resetModules();
    apiFetchMock.mockReset();
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it("fetches issues with default filter using REST endpoint", async () => {
    vi.useRealTimers();
    const issues = [
      { id: 1, number: 1, title: "Test issue", state: "open" },
    ];
    apiFetchMock.mockResolvedValueOnce(mockResponse(issues));

    const useGitHubIssues = await loadUseGitHubIssues();
    const { result } = renderHook(() =>
      useGitHubIssues("acme", "my-project", DEFAULT_ISSUE_FILTER)
    );

    await waitFor(() => {
      expect(result.current.issues).toHaveLength(1);
      expect(result.current.isLoading).toBe(false);
    });

    expect(apiFetchMock).toHaveBeenCalledWith(
      expect.stringContaining("/api/integrations/github/repos/acme/my-project/issues?")
    );
    // Should not call search endpoint
    expect(apiFetchMock).not.toHaveBeenCalledWith(
      expect.stringContaining("/search")
    );
  });

  it("forwards labels as comma-separated string in REST mode", async () => {
    vi.useRealTimers();
    apiFetchMock.mockResolvedValueOnce(mockResponse([]));

    const filter: IssueFilterState = {
      ...DEFAULT_ISSUE_FILTER,
      labels: ["bug", "enhancement"],
    };

    const useGitHubIssues = await loadUseGitHubIssues();
    renderHook(() => useGitHubIssues("acme", "my-project", filter));

    await waitFor(() => {
      expect(apiFetchMock).toHaveBeenCalled();
    });

    const calledUrl = apiFetchMock.mock.calls[0][0] as string;
    expect(calledUrl).toContain("labels=bug%2Cenhancement");
  });

  it("forwards author as creator param in REST mode", async () => {
    vi.useRealTimers();
    apiFetchMock.mockResolvedValueOnce(mockResponse([]));

    const filter: IssueFilterState = {
      ...DEFAULT_ISSUE_FILTER,
      author: "octocat",
    };

    const useGitHubIssues = await loadUseGitHubIssues();
    renderHook(() => useGitHubIssues("acme", "my-project", filter));

    await waitFor(() => {
      expect(apiFetchMock).toHaveBeenCalled();
    });

    const calledUrl = apiFetchMock.mock.calls[0][0] as string;
    expect(calledUrl).toContain("creator=octocat");
  });

  it("switches to search endpoint when search is present (after debounce)", async () => {
    vi.useRealTimers();
    const searchResult = {
      total_count: 1,
      incomplete_results: false,
      items: [{ id: 1, number: 1, title: "Bug fix", state: "open" }],
    };
    // First call may be REST (empty debounced search), second call is search
    apiFetchMock.mockResolvedValue(mockResponse(searchResult));

    const filter: IssueFilterState = {
      ...DEFAULT_ISSUE_FILTER,
      search: "bug fix",
    };

    const useGitHubIssues = await loadUseGitHubIssues();
    renderHook(() =>
      useGitHubIssues("acme", "my-project", filter)
    );

    // Wait for the debounce (300ms) + fetch to complete
    await waitFor(
      () => {
        const searchCalls = apiFetchMock.mock.calls.filter((call: unknown[]) =>
          (call[0] as string).includes("/search")
        );
        expect(searchCalls.length).toBeGreaterThanOrEqual(1);
      },
      { timeout: 2000 }
    );
  });

  it("filters out pull requests from results", async () => {
    vi.useRealTimers();
    const mixed = [
      { id: 1, number: 1, title: "Issue", state: "open" },
      { id: 2, number: 2, title: "PR", state: "open", pull_request: { url: "..." } },
    ];
    apiFetchMock.mockResolvedValueOnce(mockResponse(mixed));

    const useGitHubIssues = await loadUseGitHubIssues();
    const { result } = renderHook(() =>
      useGitHubIssues("acme", "my-project", DEFAULT_ISSUE_FILTER)
    );

    await waitFor(() => {
      expect(result.current.issues).toHaveLength(1);
      expect(result.current.issues[0]?.title).toBe("Issue");
    });
  });

  it("resets page when filter changes", async () => {
    vi.useRealTimers();
    apiFetchMock.mockResolvedValue(mockResponse([]));

    const useGitHubIssues = await loadUseGitHubIssues();
    const { rerender } = renderHook(
      ({ filter }) => useGitHubIssues("acme", "my-project", filter),
      { initialProps: { filter: DEFAULT_ISSUE_FILTER } }
    );

    await waitFor(() => {
      expect(apiFetchMock).toHaveBeenCalled();
    });

    // Change filter — should reset to page 1
    const newFilter: IssueFilterState = {
      ...DEFAULT_ISSUE_FILTER,
      state: "closed",
    };
    rerender({ filter: newFilter });

    await waitFor(() => {
      // The second call should have page=1
      const lastCall = apiFetchMock.mock.calls[apiFetchMock.mock.calls.length - 1][0] as string;
      expect(lastCall).toContain("page=1");
      expect(lastCall).toContain("state=closed");
    });
  });

  it("handles error responses", async () => {
    vi.useRealTimers();
    apiFetchMock.mockResolvedValueOnce({
      ok: false,
      json: async () => ({ error: "Not Found" }),
    });

    const useGitHubIssues = await loadUseGitHubIssues();
    const { result } = renderHook(() =>
      useGitHubIssues("acme", "my-project", DEFAULT_ISSUE_FILTER)
    );

    await waitFor(() => {
      expect(result.current.error).toBeTruthy();
      expect(result.current.isLoading).toBe(false);
    });
  });

  it("does not fetch when owner/repo are null", async () => {
    vi.useRealTimers();
    const useGitHubIssues = await loadUseGitHubIssues();
    renderHook(() => useGitHubIssues(null, null, DEFAULT_ISSUE_FILTER));

    // Give a tick
    await new Promise((r) => setTimeout(r, 50));
    expect(apiFetchMock).not.toHaveBeenCalled();
  });
});
