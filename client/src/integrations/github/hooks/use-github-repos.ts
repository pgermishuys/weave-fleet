"use client";

import { useCallback, useEffect, useSyncExternalStore } from "react";
import { apiFetch } from "@/lib/api-client";
import type { GitHubRepo, CachedGitHubRepo } from "../types";
import { clearLegacyGitHubRepoCacheOnce } from "../storage";

const PER_PAGE = 100;
const SESSION_CACHE_MAX_AGE_MS = 15 * 60 * 1000;

interface RepoCacheState {
  repos: CachedGitHubRepo[];
  isLoading: boolean;
  error: string | null;
  lastUpdated: number | null;
}

let state: RepoCacheState = {
  repos: [],
  isLoading: false,
  error: null,
  lastUpdated: null,
};

let inFlightFetch: Promise<void> | null = null;
let cacheGeneration = 0;

const listeners = new Set<() => void>();

function emitChange() {
  for (const listener of listeners) {
    listener();
  }
}

function setState(patch: Partial<RepoCacheState>) {
  state = { ...state, ...patch };
  emitChange();
}

function subscribe(callback: () => void) {
  listeners.add(callback);
  return () => {
    listeners.delete(callback);
  };
}

function getSnapshot() {
  return state;
}

export interface UseGitHubReposResult {
  repos: CachedGitHubRepo[];
  isLoading: boolean;
  error: string | null;
  lastUpdated: number | null;
  isStale: boolean;
  refresh: () => void;
  clear: () => void;
}

function toCache(repo: GitHubRepo): CachedGitHubRepo {
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

export function useGitHubRepos(): UseGitHubReposResult {
  const cache = useSyncExternalStore(subscribe, getSnapshot, getSnapshot);

  useEffect(() => {
    clearLegacyGitHubRepoCacheOnce();
  }, []);

  const fetchAll = useCallback(async () => {
    if (inFlightFetch) {
      await inFlightFetch;
      return;
    }

    const generation = cacheGeneration;

    inFlightFetch = (async () => {
      setState({ isLoading: true, error: null });

      try {
        let page = 1;
        const all: CachedGitHubRepo[] = [];

        while (true) {
          const res = await apiFetch(
            `/api/integrations/github/repos?page=${page}&per_page=${PER_PAGE}&sort=updated`
          );
          if (!res.ok) {
            throw new Error("Failed to fetch repositories");
          }

          const data: GitHubRepo[] = await res.json();
          all.push(...data.map(toCache));

          if (data.length < PER_PAGE) {
            break;
          }
          page++;
        }

        if (generation !== cacheGeneration) {
          return;
        }

        setState({
          repos: all,
          lastUpdated: Date.now(),
          isLoading: false,
          error: null,
        });
      } catch (err: unknown) {
        if (generation !== cacheGeneration) {
          return;
        }

        setState({
          isLoading: false,
          error:
            err instanceof Error ? err.message : "Failed to load repositories",
        });
      } finally {
        inFlightFetch = null;
      }
    })();

    await inFlightFetch;
  }, []);

  const isStale =
    cache.lastUpdated === null ||
    Date.now() - cache.lastUpdated > SESSION_CACHE_MAX_AGE_MS;

  const refresh = useCallback(() => {
    void fetchAll();
  }, [fetchAll]);

  const clear = useCallback(() => {
    cacheGeneration += 1;
    inFlightFetch = null;
    setState({ repos: [], isLoading: false, error: null, lastUpdated: null });
  }, []);

  return {
    repos: cache.repos,
    isLoading: cache.isLoading,
    error: cache.error,
    lastUpdated: cache.lastUpdated,
    isStale,
    refresh,
    clear,
  };
}
