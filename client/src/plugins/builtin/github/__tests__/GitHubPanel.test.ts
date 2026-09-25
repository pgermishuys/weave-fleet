import { flushPromises, mount } from "@vue/test-utils";
import { beforeEach, describe, expect, it, vi } from "vitest";

const routerNavigateMock = vi.fn();

const authState = vi.hoisted(() => ({
  isConnected: null as unknown as { value: boolean },
  isLoadingStatus: null as unknown as { value: boolean },
}));

const bookmarkState = vi.hoisted(() => ({
  bookmarks: null as unknown as { value: Array<{ fullName: string; owner: string; name: string }> },
  isLoading: null as unknown as { value: boolean },
  error: null as unknown as { value: string | null },
  refresh: vi.fn(async () => undefined),
  addBookmark: vi.fn(async () => undefined),
  removeBookmark: vi.fn(async (fullName: string) => {
    bookmarkState.bookmarks.value = bookmarkState.bookmarks.value.filter((b) => b.fullName !== fullName);
  }),
}));

vi.mock("@tanstack/vue-router", async () => {
  const { shallowRef } = await import("vue");
  return {
    useRouter: () => ({ navigate: routerNavigateMock }),
    useLocation: () => shallowRef("/github/acme/rocket/pulls/7"),
  };
});

const workState = vi.hoisted(() => ({ work: null as unknown as { value: unknown } }));
vi.mock("@/composables/use-github-work", async () => {
  const { shallowRef } = await import("vue");
  workState.work = shallowRef(null);
  return { useGitHubWork: () => ({ work: workState.work, load: vi.fn(), refresh: vi.fn() }) };
});

vi.mock("@/plugins/builtin/github/composables/use-github-auth", async () => {
  const { readonly, shallowRef } = await import("vue");

  authState.isConnected = shallowRef(true);
  authState.isLoadingStatus = shallowRef(false);

  return {
    useGitHubAuth: () => ({
      isConnected: readonly(authState.isConnected),
      isLoadingStatus: readonly(authState.isLoadingStatus),
    }),
  };
});

vi.mock("@/plugins/builtin/github/composables/use-github-bookmarks", async () => {
  const { readonly, shallowRef } = await import("vue");

  bookmarkState.bookmarks = shallowRef([
    { fullName: "acme/rocket", owner: "acme", name: "rocket" },
  ]);
  bookmarkState.isLoading = shallowRef(false);
  bookmarkState.error = shallowRef<string | null>(null);

  return {
    useGitHubBookmarks: () => ({
      bookmarks: readonly(bookmarkState.bookmarks),
      isLoading: readonly(bookmarkState.isLoading),
      error: readonly(bookmarkState.error),
      refresh: bookmarkState.refresh,
      addBookmark: bookmarkState.addBookmark,
      removeBookmark: bookmarkState.removeBookmark,
      hasBookmark: (fullName: string) => bookmarkState.bookmarks.value.some((b) => b.fullName === fullName),
    }),
  };
});

import GitHubPanel from "@/plugins/builtin/github/GitHubPanel.vue";

describe("GitHubPanel", () => {
  beforeEach(() => {
    routerNavigateMock.mockReset();
    bookmarkState.removeBookmark.mockClear();

    authState.isConnected.value = true;
    authState.isLoadingStatus.value = false;
    bookmarkState.bookmarks.value = [
      { fullName: "acme/rocket", owner: "acme", name: "rocket" },
    ];
    bookmarkState.error.value = null;
  });

  it("marks only the repository on screen, not one whose name it starts with", async () => {
    bookmarkState.bookmarks.value = [
      { fullName: "acme/rock", owner: "acme", name: "rock" },
      { fullName: "acme/rocket", owner: "acme", name: "rocket" },
    ];

    const wrapper = mount(GitHubPanel);
    await flushPromises();

    const rows = wrapper.findAll(".bookmark-link");
    expect(rows.map((row) => row.classes().includes("panel-row--current"))).toEqual([false, true]);
  });

  it("shows bookmarked repos and navigates on click", async () => {
    const wrapper = mount(GitHubPanel);
    await flushPromises();

    expect(wrapper.text()).toContain("acme/rocket");

    await wrapper.get(".bookmark-link").trigger("click");

    expect(routerNavigateMock).toHaveBeenCalledWith({
      to: "/github/acme/rocket",
    });
  });

  it("removes a bookmark on X click", async () => {
    const wrapper = mount(GitHubPanel);
    await flushPromises();

    await wrapper.get(".bookmark-remove").trigger("click");
    await flushPromises();

    expect(bookmarkState.removeBookmark).toHaveBeenCalledWith("acme/rocket");
  });

  it("shows empty state when no bookmarks", async () => {
    bookmarkState.bookmarks.value = [];

    const wrapper = mount(GitHubPanel);
    await flushPromises();

    expect(wrapper.text()).toContain("Follow a repository to see its pull requests and issues here.");
  });

  it("shows the account, what needs you and each repository's open counts", async () => {
    const pull = (number: number, extra: Record<string, unknown> = {}) => ({
      kind: "pull", owner: "acme", repo: "rocket", number, title: "t", url: "", state: "open", isDraft: false,
      author: "pat", authorAvatarUrl: null, createdAt: "", updatedAt: "", comments: 0, labels: [], headRef: "x", baseRef: "main",
      additions: 1, deletions: 1, checks: "success", reviewDecision: null, mergeable: "MERGEABLE", reviewers: [], assignees: [], ...extra,
    });
    workState.work.value = {
      login: "pat",
      avatarUrl: null,
      reviewRequested: [pull(1)],
      authored: [pull(2, { checks: "failure" }), pull(3)],
      assigned: [],
      repos: [{ fullName: "acme/rocket", openPullRequests: 5, openIssues: 6 }],
    };

    const wrapper = mount(GitHubPanel);
    await flushPromises();

    expect(wrapper.text()).toContain("pat");
    expect(wrapper.text()).toContain("Connected");
    expect(wrapper.get('[data-testid="github-panel-home"]').text()).toContain("2");
    const repo = wrapper.get(".bookmark-link");
    expect(repo.text()).toContain("5 · 6");
    expect(repo.classes()).toContain("panel-row--current");
    workState.work.value = null;
  });
});
