import {
  computed,
  onUnmounted,
  readonly,
  shallowRef,
  toValue,
  watch,
  type ComputedRef,
  type MaybeRefOrGetter,
  type ShallowRef,
} from "vue";
import { apiFetch } from "@/lib/api-client";
import type { GitHubAssignee, GitHubLabel, GitHubMilestone } from "./github-types";

const CACHE_TTL_MS = 5 * 60 * 1000;

interface RepoMetadataCacheEntry<T> {
  data: T[];
  isLoading: boolean;
  error: string | null;
  lastUpdated: number | null;
}

interface RepoMetadataStore<T> {
  cache: Map<string, RepoMetadataCacheEntry<T>>;
  listeners: Map<string, Set<() => void>>;
  inFlight: Map<string, Promise<void>>;
  generations: Map<string, number>;
}

export interface UseGitHubRepoMetadataOptions {
  owner: MaybeRefOrGetter<string | null>;
  repo: MaybeRefOrGetter<string | null>;
  immediate?: MaybeRefOrGetter<boolean>;
}

export interface UseGitHubRepoMetadataResult<T> {
  data: Readonly<ShallowRef<readonly T[]>>;
  isLoading: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | null>>;
  lastUpdated: Readonly<ShallowRef<number | null>>;
  isStale: Readonly<ComputedRef<boolean>>;
  refresh: () => Promise<void>;
}

interface GitHubMetadataConfig<T> {
  name: string;
  endpoint: (owner: string, repo: string) => string;
}

function defaultEntry<T>(): RepoMetadataCacheEntry<T> {
  return {
    data: [],
    isLoading: false,
    error: null,
    lastUpdated: null,
  };
}

function createStore<T>(): RepoMetadataStore<T> {
  return {
    cache: new Map<string, RepoMetadataCacheEntry<T>>(),
    listeners: new Map<string, Set<() => void>>(),
    inFlight: new Map<string, Promise<void>>(),
    generations: new Map<string, number>(),
  };
}

async function readErrorMessage(response: Response, fallback: string): Promise<string> {
  const payload = (await response.json().catch(() => ({}))) as { error?: string; message?: string };
  return payload.error ?? payload.message ?? fallback;
}

function isEntryStale(entry: RepoMetadataCacheEntry<unknown>): boolean {
  return entry.lastUpdated === null || Date.now() - entry.lastUpdated > CACHE_TTL_MS;
}

export function createGitHubMetadataComposable<T>(config: GitHubMetadataConfig<T>) {
  const store = createStore<T>();

  function getEntry(key: string): RepoMetadataCacheEntry<T> {
    return store.cache.get(key) ?? defaultEntry<T>();
  }

  function setEntry(key: string, patch: Partial<RepoMetadataCacheEntry<T>>): void {
    const nextEntry = { ...getEntry(key), ...patch };
    store.cache.set(key, nextEntry);
    store.listeners.get(key)?.forEach((listener) => listener());
  }

  function subscribe(key: string, listener: () => void): () => void {
    let listeners = store.listeners.get(key);
    if (!listeners) {
      listeners = new Set();
      store.listeners.set(key, listeners);
    }

    listeners.add(listener);

    return () => {
      const currentListeners = store.listeners.get(key);
      currentListeners?.delete(listener);
      if (currentListeners && currentListeners.size === 0) {
        store.listeners.delete(key);
      }
    };
  }

  async function fetchData(owner: string, repo: string, force = false): Promise<void> {
    const key = `${owner}/${repo}`;
    const existingEntry = getEntry(key);
    if (!force && !isEntryStale(existingEntry)) {
      return;
    }

    const inFlightRequest = store.inFlight.get(key);
    if (inFlightRequest) {
      await inFlightRequest;
      return;
    }

    const generation = (store.generations.get(key) ?? 0) + 1;
    store.generations.set(key, generation);

    const request = (async () => {
      setEntry(key, { isLoading: true, error: null });

      try {
        const response = await apiFetch(config.endpoint(owner, repo));
        if (!response.ok) {
          throw new Error(await readErrorMessage(response, `Failed to fetch ${config.name}.`));
        }

        const payload = (await response.json()) as T[];
        if (store.generations.get(key) !== generation) {
          return;
        }

        setEntry(key, {
          data: payload,
          isLoading: false,
          error: null,
          lastUpdated: Date.now(),
        });
      } catch (fetchError) {
        if (store.generations.get(key) !== generation) {
          return;
        }

        setEntry(key, {
          isLoading: false,
          error: fetchError instanceof Error ? fetchError.message : `Failed to load ${config.name}.`,
        });
      } finally {
        store.inFlight.delete(key);
      }
    })();

    store.inFlight.set(key, request);
    await request;
  }

  return function useGitHubRepoMetadata(
    options: UseGitHubRepoMetadataOptions,
  ): UseGitHubRepoMetadataResult<T> {
    const data = shallowRef<T[]>([]);
    const isLoading = shallowRef(false);
    const error = shallowRef<string | null>(null);
    const lastUpdated = shallowRef<number | null>(null);

    const owner = computed(() => toValue(options.owner));
    const repo = computed(() => toValue(options.repo));
    const immediate = computed(() => toValue(options.immediate) ?? true);
    const repoKey = computed(() => (owner.value && repo.value ? `${owner.value}/${repo.value}` : null));

    let unsubscribe: (() => void) | undefined;

    function syncFromCache(key: string | null): void {
      if (!key) {
        data.value = [];
        isLoading.value = false;
        error.value = null;
        lastUpdated.value = null;
        return;
      }

      const entry = getEntry(key);
      data.value = entry.data;
      isLoading.value = entry.isLoading;
      error.value = entry.error;
      lastUpdated.value = entry.lastUpdated;
    }

    watch(
      repoKey,
      (key) => {
        unsubscribe?.();
        unsubscribe = undefined;

        syncFromCache(key);
        if (!key) {
          return;
        }

        unsubscribe = subscribe(key, () => {
          syncFromCache(key);
        });

        const currentEntry = getEntry(key);
        if (immediate.value && isEntryStale(currentEntry) && !currentEntry.isLoading && owner.value && repo.value) {
          void fetchData(owner.value, repo.value);
        }
      },
      { immediate: true },
    );

    watch(immediate, (shouldFetch) => {
      if (!shouldFetch || !owner.value || !repo.value) {
        return;
      }

      const key = repoKey.value;
      if (!key) {
        return;
      }

      const currentEntry = getEntry(key);
      if (isEntryStale(currentEntry) && !currentEntry.isLoading) {
        void fetchData(owner.value, repo.value);
      }
    });

    onUnmounted(() => {
      unsubscribe?.();
    });

    return {
      data: data as ShallowRef<readonly T[]>,
      isLoading: readonly(isLoading),
      error: readonly(error),
      lastUpdated: readonly(lastUpdated),
      isStale: computed(() => lastUpdated.value === null || Date.now() - lastUpdated.value > CACHE_TTL_MS),
      refresh: async () => {
        if (owner.value && repo.value) {
          await fetchData(owner.value, repo.value, true);
        }
      },
    };
  };
}

export const useGitHubLabels = createGitHubMetadataComposable<GitHubLabel>({
  name: "labels",
  endpoint: (owner, repo) => `/api/integrations/github/repos/${owner}/${repo}/labels`,
});

export const useGitHubMilestones = createGitHubMetadataComposable<GitHubMilestone>({
  name: "milestones",
  endpoint: (owner, repo) => `/api/integrations/github/repos/${owner}/${repo}/milestones`,
});

export const useGitHubAssignees = createGitHubMetadataComposable<GitHubAssignee>({
  name: "assignees",
  endpoint: (owner, repo) => `/api/integrations/github/repos/${owner}/${repo}/assignees`,
});
