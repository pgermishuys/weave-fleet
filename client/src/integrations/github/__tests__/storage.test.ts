// @vitest-environment jsdom

import { describe, expect, it } from "vitest";
import {
  GITHUB_BOOKMARKED_REPOS_KEY,
  GITHUB_LAST_REPO_KEY,
  GITHUB_REPOS_CACHE_KEY,
  GITHUB_REPOS_CACHE_TS_KEY,
  clearGitHubClientState,
  clearLegacyGitHubRepoCacheOnce,
} from "@/integrations/github/storage";

describe("github storage helpers", () => {
  it("clears legacy repo cache keys", () => {
    localStorage.setItem(GITHUB_REPOS_CACHE_KEY, "[]");
    localStorage.setItem(GITHUB_REPOS_CACHE_TS_KEY, "123");

    clearLegacyGitHubRepoCacheOnce();

    expect(localStorage.getItem(GITHUB_REPOS_CACHE_KEY)).toBeNull();
    expect(localStorage.getItem(GITHUB_REPOS_CACHE_TS_KEY)).toBeNull();
  });

  it("clears all weave:github keys", () => {
    localStorage.setItem(GITHUB_BOOKMARKED_REPOS_KEY, "[]");
    localStorage.setItem(GITHUB_LAST_REPO_KEY, "{}");
    localStorage.setItem(GITHUB_REPOS_CACHE_KEY, "[]");
    localStorage.setItem(GITHUB_REPOS_CACHE_TS_KEY, "123");
    localStorage.setItem("weave:github:future", "value");

    clearGitHubClientState();

    expect(localStorage.getItem(GITHUB_BOOKMARKED_REPOS_KEY)).toBeNull();
    expect(localStorage.getItem(GITHUB_LAST_REPO_KEY)).toBeNull();
    expect(localStorage.getItem(GITHUB_REPOS_CACHE_KEY)).toBeNull();
    expect(localStorage.getItem(GITHUB_REPOS_CACHE_TS_KEY)).toBeNull();
    expect(localStorage.getItem("weave:github:future")).toBeNull();
  });
});
