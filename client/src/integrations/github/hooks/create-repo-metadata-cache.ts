"use client";

import { useCallback, useMemo, useSyncExternalStore } from "react";
import { apiFetch } from "@/lib/api-client";

// ─── Per-repo metadata cache factory ──────────────────────────────────────────
//
// Replicates the `useGitHubRepos` caching pattern (module-level state +
// useSyncExternalStore + listeners + generation counter) but **keyed per
// `owner/repo`** since metadata differs between repos.
//
// Each call to `createRepoMetadataCache` produces an independent module-level
// cache (Map<string, CacheEntry<T>>) and a React hook that reads/writes it.

const CACHE_MAX_AGE_MS = 5 * 60 * 1000; // 5 minutes

export interface CacheEntry<T> {
  data: T[];
  isLoading: boolean;
  error: string | null;
  lastUpdated: number | null;
}

function defaultEntry<T>(): CacheEntry<T> {
  return { data: [], isLoading: false, error: null, lastUpdated: null };
}

export interface RepoMetadataCacheResult<T> {
  data: T[];
  isLoading: boolean;
  error: string | null;
  isStale: boolean;
  refresh: () => void;
}

interface CacheConfig<T> {
  /** API endpoint builder — e.g. `(o, r) => `/api/integrations/github/repos/${o}/${r}/labels`` */
  endpoint: (owner: string, repo: string) => string;
  /** Human-readable name for error messages */
  name: string;
  /** Optional transform applied to the raw JSON array */
  transform?: (raw: unknown[]) => T[];
}

export function createRepoMetadataCache<T>(config: CacheConfig<T>) {
  const cache = new Map<string, CacheEntry<T>>();
  const listeners = new Set<() => void>();
  const inFlight = new Map<string, Promise<void>>();
  const generations = new Map<string, number>();

  function emitChange() {
    for (const fn of listeners) fn();
  }

  function getEntry(key: string): CacheEntry<T> {
    return cache.get(key) ?? defaultEntry<T>();
  }

  function subscribe(callback: () => void) {
    listeners.add(callback);
    return () => {
      listeners.delete(callback);
    };
  }

  // Snapshot is a reference to the entire Map — React detects changes via
  // identity (we rebuild entries on every setEntry).
  let snapshotVersion = 0;
  let snapshotRef: { version: number; map: Map<string, CacheEntry<T>> } = {
    version: 0,
    map: cache,
  };

  function getSnapshot() {
    if (snapshotRef.version !== snapshotVersion) {
      snapshotRef = { version: snapshotVersion, map: cache };
    }
    return snapshotRef;
  }

  // Increment snapshot version on every change so useSyncExternalStore re-renders
  const originalEmit = emitChange;
  function emitChangeWithVersion() {
    snapshotVersion++;
    originalEmit();
  }

  // Re-wire emitChange to include version bump
  function setEntryV(key: string, patch: Partial<CacheEntry<T>>) {
    const prev = getEntry(key);
    cache.set(key, { ...prev, ...patch });
    emitChangeWithVersion();
  }

  async function fetchData(owner: string, repo: string, force = false) {
    const key = `${owner}/${repo}`;
    const entry = getEntry(key);

    // Skip if fresh and not forced
    if (
      !force &&
      entry.lastUpdated !== null &&
      Date.now() - entry.lastUpdated < CACHE_MAX_AGE_MS
    ) {
      return;
    }

    // Deduplicate concurrent fetches for the same repo
    if (inFlight.has(key)) {
      await inFlight.get(key);
      return;
    }

    const gen = (generations.get(key) ?? 0) + 1;
    generations.set(key, gen);

    const promise = (async () => {
      setEntryV(key, { isLoading: true, error: null });

      try {
        const res = await apiFetch(config.endpoint(owner, repo));
        if (!res.ok) {
          throw new Error(`Failed to fetch ${config.name}`);
        }

        const raw: unknown[] = await res.json();
        const data = config.transform ? config.transform(raw) : (raw as T[]);

        // Guard against stale response
        if (generations.get(key) !== gen) return;

        setEntryV(key, {
          data,
          isLoading: false,
          error: null,
          lastUpdated: Date.now(),
        });
      } catch (err: unknown) {
        if (generations.get(key) !== gen) return;

        setEntryV(key, {
          isLoading: false,
          error:
            err instanceof Error ? err.message : `Failed to load ${config.name}`,
        });
      } finally {
        inFlight.delete(key);
      }
    })();

    inFlight.set(key, promise);
    await promise;
  }

  /**
   * React hook that returns cached metadata for the given repo.
   * Auto-fetches on mount if stale or missing.
   */
  function useRepoMetadata(
    owner: string | null,
    repo: string | null
  ): RepoMetadataCacheResult<T> {
    const snapshot = useSyncExternalStore(subscribe, getSnapshot, getSnapshot);

    const key = owner && repo ? `${owner}/${repo}` : null;
    const entry = key ? (snapshot.map.get(key) ?? defaultEntry<T>()) : defaultEntry<T>();

    const isStale =
      entry.lastUpdated === null ||
      Date.now() - entry.lastUpdated > CACHE_MAX_AGE_MS;

    const refresh = useCallback(() => {
      if (owner && repo) {
        void fetchData(owner, repo, true);
      }
    }, [owner, repo]);

    // Auto-fetch on mount when stale
    const stableOwner = owner;
    const stableRepo = repo;
    useMemo(() => {
      if (stableOwner && stableRepo && isStale && !entry.isLoading) {
        void fetchData(stableOwner, stableRepo);
      }
      // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [stableOwner, stableRepo]);

    return {
      data: entry.data,
      isLoading: entry.isLoading,
      error: entry.error,
      isStale,
      refresh,
    };
  }

  return { useRepoMetadata, fetchData };
}
