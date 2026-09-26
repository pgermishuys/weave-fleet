import { flushPromises, mount } from "@vue/test-utils";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { _resetGitHubWorkForTesting } from "@/composables/use-github-work";

const { apiFetchMock, mockNavigate, auth } = vi.hoisted(() => ({
  apiFetchMock: vi.fn(),
  mockNavigate: vi.fn(),
  auth: { connected: true, connect: vi.fn() },
}));

vi.mock("@/lib/api-client", () => ({ apiFetch: apiFetchMock }));
vi.mock("@/composables/use-sessions", () => ({ useSessions: () => ({}) }));
vi.mock("@tanstack/vue-router", () => ({
  useRouter: () => ({ navigate: mockNavigate }),
  useLocation: () => ({ value: "/github" }),
}));
vi.mock("@/plugins/builtin/github/composables/use-github-auth", async () => {
  const { shallowRef, computed } = await import("vue");
  return {
    useGitHubAuth: () => ({
      isConnected: shallowRef(auth.connected),
      isLoadingStatus: shallowRef(false),
      deviceState: shallowRef({ status: "idle" }),
      isAwaitingAuthorization: computed(() => false),
      connectWithDeviceFlow: auth.connect,
      resetDeviceFlow: vi.fn(),
      copyUserCode: vi.fn(),
    }),
  };
});
vi.mock("@/plugins/builtin/github/composables/use-github-bookmarks", async () => {
  const { shallowRef } = await import("vue");
  return { useGitHubBookmarks: () => ({ bookmarks: shallowRef([{ fullName: "acme/rocket", owner: "acme", name: "rocket" }]) }) };
});

import GitHubBrowserPage from "@/components/pages/GitHubBrowserPage.vue";

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

function item(number: number, extra: Record<string, unknown> = {}) {
  return {
    kind: "pull", owner: "acme", repo: "rocket", number, title: `Item ${number}`, url: "", state: "open", isDraft: false,
    author: "pat", authorAvatarUrl: null, createdAt: "2026-09-25T00:00:00Z", updatedAt: "2026-09-25T00:00:00Z", comments: 0,
    labels: [], headRef: "x", baseRef: "main", additions: 1, deletions: 1, checks: "success", reviewDecision: null,
    mergeable: "MERGEABLE", reviewers: [], assignees: [], ...extra,
  };
}

describe("GitHubBrowserPage", () => {
  beforeEach(() => {
    _resetGitHubWorkForTesting();
    auth.connected = true;
    auth.connect.mockReset();
    apiFetchMock.mockReset();
    mockNavigate.mockReset();
    apiFetchMock.mockImplementation(async (url: string) => {
      if (url === "/api/smart-links") return json([]);
      if (url.endsWith("/work")) {
        return json({
          login: "pat",
          avatarUrl: null,
          reviewRequested: [item(1)],
          authored: [item(2, { checks: "failure" }), item(3)],
          assigned: [item(4, { kind: "issue", checks: null, headRef: null, additions: null, deletions: null })],
          repos: [],
        });
      }
      return json([]);
    });
  });

  it("puts review requests and blocked pull requests under Needs you, each with why", async () => {
    const wrapper = mount(GitHubBrowserPage);
    await flushPromises();

    const needs = wrapper.get('[data-testid="github-needs-you"]');
    const rows = needs.findAll('[data-testid="github-row"]');
    expect(rows.map((row) => row.text())).toEqual([
      expect.stringContaining("Review requested"),
      expect.stringContaining("Checks failing"),
    ]);
    expect(wrapper.text()).toContain("Your pull requests");
    expect(wrapper.text()).toContain("Item 3");
    expect(wrapper.text()).toContain("Assigned to you");
    expect(wrapper.text()).toContain("Item 4");
  });

  it("starts a session from a row", async () => {
    const wrapper = mount(GitHubBrowserPage);
    await flushPromises();

    await wrapper.get('[data-testid="github-row-start"]').trigger("click");
    await flushPromises();

    expect(mockNavigate).toHaveBeenCalledWith({ to: "/sessions/new", search: { projectId: undefined, source: undefined } });
  });

  it("explains what connecting gets you and connects from the page", async () => {
    auth.connected = false;
    const wrapper = mount(GitHubBrowserPage);
    await flushPromises();

    expect(wrapper.get('[data-testid="github-connect"]').text()).toContain("See which pull requests need you");
    await wrapper.get('[data-testid="github-connect-start"]').trigger("click");
    expect(auth.connect).toHaveBeenCalled();
    expect(apiFetchMock).not.toHaveBeenCalledWith("/api/integrations/github/work", expect.anything());
  });
});
