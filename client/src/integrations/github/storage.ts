"use client";

import { removePersistedKey } from "@/hooks/use-persisted-state";

export const GITHUB_BOOKMARKED_REPOS_KEY = "weave:github:repos";
export const GITHUB_LAST_REPO_KEY = "weave:github:lastRepo";
export const GITHUB_REPOS_CACHE_KEY = "weave:github:repos-cache";
export const GITHUB_REPOS_CACHE_TS_KEY = "weave:github:repos-cache-ts";

let legacyCacheCleared = false;

export function clearLegacyGitHubRepoCacheOnce(): void {
  if (legacyCacheCleared) return;
  legacyCacheCleared = true;

  removePersistedKey(GITHUB_REPOS_CACHE_KEY);
  removePersistedKey(GITHUB_REPOS_CACHE_TS_KEY);
}

export function clearGitHubClientState(): void {
  removePersistedKey(GITHUB_BOOKMARKED_REPOS_KEY);
  removePersistedKey(GITHUB_LAST_REPO_KEY);
  removePersistedKey(GITHUB_REPOS_CACHE_KEY);
  removePersistedKey(GITHUB_REPOS_CACHE_TS_KEY);

  try {
    const keysToRemove: string[] = [];
    for (let i = 0; i < localStorage.length; i++) {
      const key = localStorage.key(i);
      if (key !== null && key.startsWith("weave:github:")) {
        keysToRemove.push(key);
      }
    }

    for (const key of keysToRemove) {
      removePersistedKey(key);
    }
  } catch {
    // localStorage unavailable
  }
}
