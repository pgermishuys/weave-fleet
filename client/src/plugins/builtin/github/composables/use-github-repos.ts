import { computed, onMounted, readonly, shallowRef, type ComputedRef, type ShallowRef } from "vue";
import { apiFetch } from "@/lib/api-client";
import type { CachedGitHubRepo, GitHubRepo } from "./github-types";

const PER_PAGE = 100;
const CACHE_TTL_MS = 15 * 60 * 1000;
const LEGACY_REPOS_CACHE_KEY = "weave:github:repos-cache";
const LEGACY_REPOS_CACHE_TS_KEY = "weave:github:repos-cache-ts";

const repos = shallowRef<CachedGitHubRepo[]>([]);
const isLoading = shallowRef(false);
const error = shallowRef<string | null>(null);
const lastUpdated = shallowRef<number | null>(null);

let inFlightFetch: Promise<void> | null = null;
let cacheGeneration = 0;
let legacyCacheCleared = false;

export interface UseGitHubReposResult {
  repos: Readonly<ShallowRef<readonly CachedGitHubRepo[]>>;
  isLoading: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | null>>;
  lastUpdated: Readonly<ShallowRef<number | null>>;
  isStale: Readonly<ComputedRef<boolean>>;
  refresh: (force?: boolean) => Promise<void>;
  clear: () => void;
}

export interface UseGitHubReposOptions {
  autoLoad?: boolean;
}

function clearLegacyGitHubRepoCacheOnce(): void {
  if (legacyCacheCleared || typeof window === "undefined") {
    return;
  }

  legacyCacheCleared = true;

  try {
    localStorage.removeItem(LEGACY_REPOS_CACHE_KEY);
    localStorage.removeItem(LEGACY_REPOS_CACHE_TS_KEY);
  } catch {
    // localStorage unavailable
  }
}

function toCachedRepo(repo: GitHubRepo): CachedGitHubRepo {
  return {
    id: repo.id,
    full_name: repo.full_name,
    name: repo.name,
    owner_login: repo.owner.login,
    private: repo.private,
    language: repo.language,
    stargazers_count: repo.stargazers_count,
  };
}

function isCacheStale(): boolean {
  return lastUpdated.value === null || Date.now() - lastUpdated.value > CACHE_TTL_MS;
}

async function fetchGitHubRepos(force = false): Promise<void> {
  if (!force && !isCacheStale()) {
    return;
  }

  if (inFlightFetch) {
    await inFlightFetch;
    return;
  }

  const generation = ++cacheGeneration;
  inFlightFetch = (async () => {
    isLoading.value = true;
    error.value = null;

    try {
      let page = 1;
      const allRepos: CachedGitHubRepo[] = [];

      while (true) {
        const response = await apiFetch(
          `/api/integrations/github/repos?page=${page}&per_page=${PER_PAGE}&sort=updated`,
        );
        if (!response.ok) {
          const payload = (await response.json().catch(() => ({}))) as { error?: string };
          throw new Error(payload.error ?? "Failed to fetch repositories.");
        }

        const pageData = (await response.json()) as GitHubRepo[];
        allRepos.push(...pageData.map(toCachedRepo));

        if (pageData.length < PER_PAGE) {
          break;
        }

        page += 1;
      }

      if (generation !== cacheGeneration) {
        return;
      }

      repos.value = allRepos;
      lastUpdated.value = Date.now();
      error.value = null;
    } catch (fetchError) {
      if (generation !== cacheGeneration) {
        return;
      }

      error.value = fetchError instanceof Error ? fetchError.message : "Failed to load repositories.";
    } finally {
      if (generation === cacheGeneration) {
        isLoading.value = false;
      }

      inFlightFetch = null;
    }
  })();

  await inFlightFetch;
}

function clear(): void {
  cacheGeneration += 1;
  inFlightFetch = null;
  repos.value = [];
  isLoading.value = false;
  error.value = null;
  lastUpdated.value = null;
}

export function useGitHubRepos(options: UseGitHubReposOptions = {}): UseGitHubReposResult {
  const { autoLoad = true } = options;
  const isStale = computed(() => isCacheStale());

  onMounted(() => {
    clearLegacyGitHubRepoCacheOnce();

    if (autoLoad && isCacheStale()) {
      void fetchGitHubRepos();
    }
  });

  return {
    repos: readonly(repos),
    isLoading: readonly(isLoading),
    error: readonly(error),
    lastUpdated: readonly(lastUpdated),
    isStale,
    refresh: fetchGitHubRepos,
    clear,
  };
}
