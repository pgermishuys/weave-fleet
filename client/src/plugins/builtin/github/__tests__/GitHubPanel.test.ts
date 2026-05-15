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

vi.mock("@tanstack/vue-router", () => ({
  useRouter: () => ({ navigate: routerNavigateMock }),
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

    expect(wrapper.text()).toContain("No bookmarked repos");
  });
});
