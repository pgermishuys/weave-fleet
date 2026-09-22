import { computed, shallowRef, watch, type Ref } from "vue";
import { api, type HarnessCatalog } from "@/api/client";
import { toAgentOptions } from "@/composables/use-agents";
import { toModelOptions } from "@/composables/use-models";

/** How long a folder's catalog is shown without asking again; the harness is asked anyway once it's older. */
const FRESH_FOR_MS = 5 * 60_000;

interface CachedCatalog {
  catalog: HarnessCatalog;
  fetchedAt: number;
}

const cache = new Map<string, CachedCatalog>();

function cacheKey(harnessType: string, directory: string | null, profile: string | undefined): string {
  return `${harnessType}\n${directory ?? ""}\n${profile ?? ""}`;
}

/** For tests: forget every folder's catalog. */
export function clearHarnessCatalogCache(): void {
  cache.clear();
}

/**
 * Forgets `harnessType`'s catalogs in every folder, so the next composer asks again: signing in to a provider or out
 * of one changes the models the harness offers everywhere.
 */
export function forgetHarnessCatalogs(harnessType: string): void {
  for (const key of [...cache.keys()]) {
    if (key.startsWith(`${harnessType}\n`)) cache.delete(key);
  }
}

async function fetchCatalog(
  harnessType: string,
  directory: string | null,
  profile: string | undefined,
  signal: AbortSignal,
): Promise<HarnessCatalog> {
  const { data, error, response } = await api.GET("/api/harnesses/{type}/catalog", {
    params: {
      path: { type: harnessType },
      query: { ...(directory ? { directory } : {}), ...(profile ? { profile } : {}) },
    },
    signal,
  });
  if (error || !response.ok || !data) {
    const payload = error as { error?: string } | undefined;
    throw new Error(payload?.error ?? `HTTP ${response.status}`);
  }
  return data as unknown as HarnessCatalog;
}

/**
 * The agents and models `harnessType` offers in `directory` (null for a quick chat's) on `profile` (an id, "none",
 * or undefined for the default: a profile can add agents and models), before any session exists.
 * The last catalog for a folder shows at once and is refreshed when it's old. While another folder's loads, the
 * previous one stays up so the pickers don't blink; `isCurrent` says whether it's this folder's.
 */
export function useHarnessCatalog(
  harnessType: Ref<string>,
  directory: Ref<string | null>,
  profile: Ref<string | undefined> = shallowRef(undefined),
) {
  const catalog = shallowRef<HarnessCatalog | null>(null);
  const loadedKey = shallowRef<string | null>(null);
  const isLoading = shallowRef(false);
  const error = shallowRef<string | null>(null);

  const key = computed(() => (harnessType.value ? cacheKey(harnessType.value, directory.value, profile.value) : null));
  const isCurrent = computed(() => key.value !== null && loadedKey.value === key.value);

  watch(key, async (nextKey, _previous, onCleanup) => {
    error.value = null;
    if (!nextKey) {
      catalog.value = null;
      loadedKey.value = null;
      isLoading.value = false;
      return;
    }

    const cached = cache.get(nextKey);
    if (cached) {
      catalog.value = cached.catalog;
      loadedKey.value = nextKey;
      if (Date.now() - cached.fetchedAt < FRESH_FOR_MS) {
        isLoading.value = false;
        return;
      }
    }

    const controller = new AbortController();
    onCleanup(() => controller.abort());
    isLoading.value = true;
    try {
      const next = await fetchCatalog(harnessType.value, directory.value, profile.value, controller.signal);
      cache.set(nextKey, { catalog: next, fetchedAt: Date.now() });
      catalog.value = next;
      loadedKey.value = nextKey;
    } catch (fetchError) {
      if (controller.signal.aborted) {
        return;
      }
      error.value = fetchError instanceof Error ? fetchError.message : "Couldn't load agents and models";
      if (!cached) {
        catalog.value = null;
        loadedKey.value = nextKey;
      }
    } finally {
      if (!controller.signal.aborted) {
        isLoading.value = false;
      }
    }
  }, { immediate: true });

  const agents = computed(() => toAgentOptions(catalog.value?.agents ?? []));
  const models = computed(() => toModelOptions(catalog.value?.providers ?? []));
  /** Whether to offer a choice at all: the harness can list its agents and models here. */
  const isSupported = computed(() => catalog.value?.supported === true);

  return { catalog, agents, models, isSupported, isCurrent, isLoading, error };
}
