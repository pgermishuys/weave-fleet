import { flushPromises, mount } from "@vue/test-utils";
import { beforeEach, describe, expect, it, vi } from "vitest";
import GitHubRepoPage from "@/components/pages/GitHubRepoPage.vue";
import { _resetGitHubWorkForTesting } from "@/composables/use-github-work";
import type { SmartLinkWire } from "@/lib/smart-links";

const { apiFetchMock, mockNavigate } = vi.hoisted(() => ({ apiFetchMock: vi.fn(), mockNavigate: vi.fn() }));

vi.mock("@/lib/api-client", () => ({ apiFetch: apiFetchMock }));
vi.mock("@/composables/use-sessions", () => ({ useSessions: () => ({}) }));
vi.mock("@tanstack/vue-router", () => ({
  useRouter: () => ({ navigate: mockNavigate }),
  useLocation: () => ({ value: "/github/acme/rocket" }),
}));

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

function pull(number: number, title: string) {
  return {
    kind: "pull", owner: "acme", repo: "rocket", number, title, url: `https://github.com/acme/rocket/pull/${number}`,
    state: "open", isDraft: false, author: "pat", authorAvatarUrl: null, createdAt: "2026-09-25T00:00:00Z", updatedAt: "2026-09-25T00:00:00Z",
    comments: 0, labels: [{ name: "client", color: "1d76db" }], headRef: "feat/x", baseRef: "main", additions: 10, deletions: 2,
    checks: "failure", reviewDecision: null, mergeable: "MERGEABLE", reviewers: [], assignees: [],
  };
}

const link: SmartLinkWire = {
  id: "l1", sessionId: "s1", url: "https://github.com/acme/rocket/pull/9", providerId: "github", resourceType: "pull_request",
  resourceId: "acme/rocket#9", title: "acme/rocket #9: Nine", status: "open", statusLabel: "Open", metadataJson: "{}",
  isDismissed: false, isTerminal: false, createdAt: "", updatedAt: "", relationship: "own", enrichmentStatus: "resolved", lastCheckedAt: null,
};

const searches = () => apiFetchMock.mock.calls
  .map(([url]) => String(url))
  .filter((url) => url.startsWith("/api/integrations/github/search"))
  .map((url) => new URL(url, "http://x").searchParams.get("q"));

describe("GitHubRepoPage", () => {
  beforeEach(() => {
    _resetGitHubWorkForTesting();
    apiFetchMock.mockReset();
    mockNavigate.mockReset();
    apiFetchMock.mockImplementation(async (url: string) => {
      if (url === "/api/smart-links") return json([link]);
      if (url.startsWith("/api/integrations/github/search")) return json({ items: [pull(7, "Seven")], totalCount: 1, endCursor: null, hasNextPage: false });
      if (url.includes("/items?numbers=")) return json([pull(9, "Nine")]);
      if (url.endsWith("/work")) return json({ login: "pat", avatarUrl: null, reviewRequested: [], authored: [], assigned: [], repos: [{ fullName: "acme/rocket", openPullRequests: 5, openIssues: 6 }] });
      return json([]);
    });
  });

  it("lists open pull requests first, with their checks, and filters to mine", async () => {
    vi.useFakeTimers();
    try {
      const wrapper = mount(GitHubRepoPage, { props: { owner: "acme", repo: "rocket" } });
      await flushPromises();

      expect(searches()).toEqual(["repo:acme/rocket is:pr is:open sort:updated-desc"]);
      expect(wrapper.get('[role="tab"][aria-selected="true"]').text()).toContain("Pull requests");
      expect(wrapper.get('[role="tab"][aria-selected="true"]').text()).toContain("5");
      expect(wrapper.get('[data-testid="github-row"]').text()).toContain("Seven");

      await wrapper.get('[data-testid="github-pull-filter-mine"]').trigger("click");
      await vi.advanceTimersByTimeAsync(300);
      await flushPromises();

      expect(searches().at(-1)).toBe("repo:acme/rocket is:pr is:open author:@me sort:updated-desc");
      wrapper.unmount();
    } finally {
      vi.useRealTimers();
    }
  });

  it("shows the pull requests Fleet sessions are on under In Fleet", async () => {
    const wrapper = mount(GitHubRepoPage, { props: { owner: "acme", repo: "rocket" } });
    await flushPromises();

    const fleet = wrapper.get('[data-testid="github-pull-filter-fleet"]');
    expect(fleet.text()).toContain("1");
    await fleet.trigger("click");
    await flushPromises();

    expect(apiFetchMock).toHaveBeenCalledWith("/api/integrations/github/repos/acme/rocket/items?numbers=9", expect.anything());
    expect(wrapper.get('[data-testid="github-row"]').text()).toContain("Nine");
    wrapper.unmount();
  });

  it("opens the pull request's page from its row", async () => {
    const wrapper = mount(GitHubRepoPage, { props: { owner: "acme", repo: "rocket" } });
    await flushPromises();

    await wrapper.get('[data-testid="github-row"]').trigger("click");

    expect(mockNavigate).toHaveBeenCalledWith({ to: "/github/acme/rocket/pulls/7" });
    wrapper.unmount();
  });
});
