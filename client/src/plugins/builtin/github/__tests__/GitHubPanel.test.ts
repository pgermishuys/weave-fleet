import { flushPromises, mount } from "@vue/test-utils";
import { beforeEach, describe, expect, it, vi } from "vitest";

const routerNavigateMock = vi.fn();

const authState = vi.hoisted(() => ({
  isConnected: null as unknown as { value: boolean },
  isLoadingStatus: null as unknown as { value: boolean },
}));

const repoState = vi.hoisted(() => ({
  repos: null as unknown as { value: Array<{ id: number; full_name: string; name: string; owner_login: string; private: boolean; language: string | null; stargazers_count: number }> },
  isLoading: null as unknown as { value: boolean },
  error: null as unknown as { value: string | null },
  refresh: vi.fn(async () => undefined),
  clear: vi.fn(),
}));

const bookmarkState = vi.hoisted(() => ({
  bookmarks: null as unknown as { value: Array<{ fullName: string; owner: string; name: string }> },
  isLoading: null as unknown as { value: boolean },
  error: null as unknown as { value: string | null },
  refresh: vi.fn(async () => undefined),
  addBookmark: vi.fn(async (repo: { fullName: string; owner: string; name: string }) => {
    bookmarkState.bookmarks.value = [...bookmarkState.bookmarks.value, repo];
  }),
  removeBookmark: vi.fn(async (fullName: string) => {
    bookmarkState.bookmarks.value = bookmarkState.bookmarks.value.filter((bookmark) => bookmark.fullName !== fullName);
  }),
}));

const issuesState = vi.hoisted(() => ({
  issues: null as unknown as { value: Array<never> },
  isLoading: null as unknown as { value: boolean },
  isSearching: null as unknown as { value: boolean },
  error: null as unknown as { value: string | null },
  hasMore: null as unknown as { value: boolean },
  loadMore: vi.fn(),
  refetch: vi.fn(),
}));

const pullsState = vi.hoisted(() => ({
  pulls: null as unknown as { value: Array<never> },
  isLoading: null as unknown as { value: boolean },
  error: null as unknown as { value: string | null },
  hasMore: null as unknown as { value: boolean },
  loadMore: vi.fn(),
  refetch: vi.fn(),
}));

const sidebarStoreState = vi.hoisted(() => ({
  setActiveRail: vi.fn(),
}));

vi.mock("@tanstack/vue-router", () => ({
  useRouter: () => ({ navigate: routerNavigateMock }),
}));

vi.mock("@/stores/sidebar", () => ({
  useSidebarStore: () => sidebarStoreState,
}));

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

vi.mock("@/plugins/builtin/github/composables/use-github-repos", async () => {
  const { readonly, shallowRef } = await import("vue");

  repoState.repos = shallowRef([
    {
      id: 1,
      full_name: "acme/rocket",
      name: "rocket",
      owner_login: "acme",
      private: false,
      language: "TypeScript",
      stargazers_count: 10,
    },
    {
      id: 2,
      full_name: "acme/anvil",
      name: "anvil",
      owner_login: "acme",
      private: false,
      language: "Go",
      stargazers_count: 4,
    },
  ]);
  repoState.isLoading = shallowRef(false);
  repoState.error = shallowRef<string | null>(null);

  return {
    useGitHubRepos: () => ({
      repos: readonly(repoState.repos),
      isLoading: readonly(repoState.isLoading),
      error: readonly(repoState.error),
      refresh: repoState.refresh,
      clear: repoState.clear,
    }),
  };
});

vi.mock("@/plugins/builtin/github/composables/use-github-bookmarks", async () => {
  const { readonly, shallowRef } = await import("vue");

  bookmarkState.bookmarks = shallowRef([
    {
      fullName: "acme/rocket",
      owner: "acme",
      name: "rocket",
    },
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
      hasBookmark: (fullName: string) => bookmarkState.bookmarks.value.some((bookmark) => bookmark.fullName === fullName),
    }),
  };
});

vi.mock("@/plugins/builtin/github/composables/use-github-issues", async () => {
  const { readonly, shallowRef } = await import("vue");

  issuesState.issues = shallowRef([]);
  issuesState.isLoading = shallowRef(false);
  issuesState.isSearching = shallowRef(false);
  issuesState.error = shallowRef<string | null>(null);
  issuesState.hasMore = shallowRef(false);

  return {
    useGitHubIssues: () => ({
      issues: readonly(issuesState.issues),
      isLoading: readonly(issuesState.isLoading),
      isSearching: readonly(issuesState.isSearching),
      error: readonly(issuesState.error),
      hasMore: readonly(issuesState.hasMore),
      loadMore: issuesState.loadMore,
      refetch: issuesState.refetch,
    }),
  };
});

vi.mock("@/plugins/builtin/github/composables/use-github-pulls", async () => {
  const { readonly, shallowRef } = await import("vue");

  pullsState.pulls = shallowRef([]);
  pullsState.isLoading = shallowRef(false);
  pullsState.error = shallowRef<string | null>(null);
  pullsState.hasMore = shallowRef(false);

  return {
    useGitHubPulls: () => ({
      pulls: readonly(pullsState.pulls),
      isLoading: readonly(pullsState.isLoading),
      error: readonly(pullsState.error),
      hasMore: readonly(pullsState.hasMore),
      loadMore: pullsState.loadMore,
      refetch: pullsState.refetch,
    }),
  };
});

import GitHubPanel from "@/plugins/builtin/github/GitHubPanel.vue";

describe("GitHubPanel", () => {
  beforeEach(() => {
    routerNavigateMock.mockReset();
    sidebarStoreState.setActiveRail.mockReset();
    repoState.refresh.mockClear();
    repoState.clear.mockClear();
    bookmarkState.refresh.mockClear();
    bookmarkState.addBookmark.mockClear();
    bookmarkState.removeBookmark.mockClear();
    issuesState.loadMore.mockClear();
    issuesState.refetch.mockClear();
    pullsState.loadMore.mockClear();
    pullsState.refetch.mockClear();

    authState.isConnected.value = true;
    authState.isLoadingStatus.value = false;
    repoState.isLoading.value = false;
    repoState.error.value = null;
    repoState.repos.value = [
      {
        id: 1,
        full_name: "acme/rocket",
        name: "rocket",
        owner_login: "acme",
        private: false,
        language: "TypeScript",
        stargazers_count: 10,
      },
      {
        id: 2,
        full_name: "acme/anvil",
        name: "anvil",
        owner_login: "acme",
        private: false,
        language: "Go",
        stargazers_count: 4,
      },
    ];
    bookmarkState.bookmarks.value = [
      {
        fullName: "acme/rocket",
        owner: "acme",
        name: "rocket",
      },
    ];
    bookmarkState.error.value = null;
    bookmarkState.isLoading.value = false;
    issuesState.issues.value = [];
    issuesState.isLoading.value = false;
    issuesState.isSearching.value = false;
    issuesState.error.value = null;
    issuesState.hasMore.value = false;
    pullsState.pulls.value = [];
    pullsState.isLoading.value = false;
    pullsState.error.value = null;
    pullsState.hasMore.value = false;
  });

  it("toggles a repo bookmark from the selector", async () => {
    const wrapper = mount(GitHubPanel);
    await flushPromises();

    await wrapper.get("#github-repo").trigger("click");
    await wrapper.get('[data-testid="github-bookmark-toggle-acme--anvil"]').trigger("click");
    await flushPromises();

    expect(bookmarkState.addBookmark).toHaveBeenCalledWith({
      fullName: "acme/anvil",
      owner: "acme",
      name: "anvil",
    });
    expect(bookmarkState.bookmarks.value.map((bookmark) => bookmark.fullName)).toContain("acme/anvil");
  });

  it("toggles the selected repo bookmark", async () => {
    const wrapper = mount(GitHubPanel);
    await flushPromises();

    const selectedBookmarkToggle = wrapper.get('[data-testid="github-selected-bookmark-toggle"]');
    expect(selectedBookmarkToggle.text()).toContain("Bookmarked");

    await selectedBookmarkToggle.trigger("click");
    await flushPromises();

    expect(bookmarkState.removeBookmark).toHaveBeenCalledWith("acme/rocket");
    expect(bookmarkState.bookmarks.value.map((bookmark) => bookmark.fullName)).not.toContain("acme/rocket");
  });

  it("shows a bookmark error when the mutation fails", async () => {
    bookmarkState.addBookmark.mockImplementationOnce(async () => {
      throw new Error("Nope");
    });

    const wrapper = mount(GitHubPanel);
    await flushPromises();

    await wrapper.get("#github-repo").trigger("click");
    await wrapper.get('[data-testid="github-bookmark-toggle-acme--anvil"]').trigger("click");
    await flushPromises();

    expect(wrapper.text()).toContain("Nope");
  });
});
