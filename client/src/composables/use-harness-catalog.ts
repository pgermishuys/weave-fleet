import { computed, shallowRef, watch, type Ref } from "vue";
import type { ApiClient, HarnessCatalog } from "@/api/client";
import { toAgentOptions } from "@/composables/use-agents";
import { toModelOptions } from "@/composables/use-models";
import {
  isCatalogChangeFor,
  listenForCatalogChanges,
  onCatalogChange,
  type HarnessCatalogChange,
} from "@/lib/harness-catalog-changes";
import { liveTarget, useMachineTarget } from "@/lib/machine-target";
import { forgetSaved, readSaved, writeSaved } from "@/lib/saved-per-machine";

/** How long a folder's catalog is shown without asking again; the harness is asked anyway once it's older. */
const FRESH_FOR_MS = 5 * 60_000;

interface CachedCatalog {
  catalog: HarnessCatalog;
  fetchedAt: number;
}

/**
 * Each machine's catalogs, by harness, folder and profile. The newest are saved per machine, so after a reload or a
 * restart of the desktop app the pickers draw at once and are checked behind it, as they would be within 5 minutes.
 */
const caches = new Map<string, Map<string, CachedCatalog>>();
const SAVED_KIND = "harness-catalogs";
const MAX_SAVED = 12;

/** `machineKey`'s catalogs: the ones this page fetched, starting from the ones saved on the last visit. */
function cacheFor(machineKey: string): Map<string, CachedCatalog> {
  let cache = caches.get(machineKey);
  if (!cache) {
    cache = new Map();
    const saved = readSaved<Record<string, CachedCatalog>>(SAVED_KIND, machineKey);
    if (saved && typeof saved === "object") {
      for (const [key, entry] of Object.entries(saved)) {
        if (entry?.catalog && typeof entry.fetchedAt === "number") cache.set(key, entry);
      }
    }
    caches.set(machineKey, cache);
  }
  return cache;
}

function saveCache(machineKey: string): void {
  const newest = [...cacheFor(machineKey).entries()].sort(([, a], [, b]) => b.fetchedAt - a.fetchedAt).slice(0, MAX_SAVED);
  writeSaved(SAVED_KIND, machineKey, Object.fromEntries(newest));
}

function cacheKey(harnessType: string, directory: string | null, profile: string | undefined): string {
  return `${harnessType}\n${directory ?? ""}\n${profile ?? ""}`;
}

// Listens from the first catalog shown, so a cached catalog that changes while no composer is open is asked for
// again when one opens.
let stopForgetting: (() => void) | null = null;

/** For tests: forget every folder's catalog. */
export function clearHarnessCatalogCache(): void {
  caches.clear();
  forgetSaved(SAVED_KIND, liveTarget().key);
  stopForgetting?.();
  stopForgetting = null;
}

/** Forgets the cached catalogs `change` is about, so they're asked for again when shown. Changes come from the live machine. */
function forgetChanged(change: HarnessCatalogChange): void {
  const machineKey = liveTarget().key;
  const cache = cacheFor(machineKey);
  for (const key of [...cache.keys()]) {
    const [harnessType = "", directory = "", profile = ""] = key.split("\n");
    if (isCatalogChangeFor(change, harnessType, directory || null, profile || undefined)) {
      cache.delete(key);
    }
  }
  saveCache(machineKey);
}

/**
 * Forgets `harnessType`'s catalogs in every folder of the live machine, so the next composer asks again: signing in to
 * a provider or out of one changes the models the harness offers everywhere.
 */
export function forgetHarnessCatalogs(harnessType: string): void {
  const machineKey = liveTarget().key;
  const cache = cacheFor(machineKey);
  for (const key of [...cache.keys()]) {
    if (key.startsWith(`${harnessType}\n`)) cache.delete(key);
  }
  saveCache(machineKey);
}

async function fetchCatalog(
  client: ApiClient,
  harnessType: string,
  directory: string | null,
  profile: string | undefined,
  signal: AbortSignal,
): Promise<HarnessCatalog> {
  const { data, error, response } = await client.GET("/api/harnesses/{type}/catalog", {
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
 * The last catalog for a folder shows at once and is refreshed when it's old, or at once when the harness says it
 * changed (a harness that can tell pushes `harness.catalog_changed`; no polling). While another folder's loads, the
 * previous one stays up so the pickers don't blink; `isCurrent` says whether it's this folder's.
 */
export function useHarnessCatalog(
  harnessType: Ref<string>,
  directory: Ref<string | null>,
  profile: Ref<string | undefined> = shallowRef(undefined),
) {
  // The machine the page asks: the new-session box can start a session on another one.
  const target = useMachineTarget();
  const cache = cacheFor(target.key);
  const catalog = shallowRef<HarnessCatalog | null>(null);
  const loadedKey = shallowRef<string | null>(null);
  const isLoading = shallowRef(false);
  const error = shallowRef<string | null>(null);

  const key = computed(() => (harnessType.value ? cacheKey(harnessType.value, directory.value, profile.value) : null));
  const isCurrent = computed(() => key.value !== null && loadedKey.value === key.value);

  // The harness says what it offers changed: ask again if it's what's shown. The old list stays up meanwhile.
  const changes = shallowRef(0);
  stopForgetting ??= listenForCatalogChanges(forgetChanged);
  onCatalogChange((change) => {
    if (target.isLive && harnessType.value && isCatalogChangeFor(change, harnessType.value, directory.value, profile.value)) {
      changes.value += 1;
    }
  });

  watch([key, changes], async ([nextKey], _previous, onCleanup) => {
    error.value = null;
    if (!nextKey) {
      catalog.value = null;
      loadedKey.value = null;
      isLoading.value = false;
      return;
    }

    const shown = loadedKey.value === nextKey ? catalog.value : null;
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
      const next = await fetchCatalog(target.api, harnessType.value, directory.value, profile.value, controller.signal);
      cache.set(nextKey, { catalog: next, fetchedAt: Date.now() });
      saveCache(target.key);
      catalog.value = next;
      loadedKey.value = nextKey;
    } catch (fetchError) {
      if (controller.signal.aborted) {
        return;
      }
      error.value = fetchError instanceof Error ? fetchError.message : "Couldn't load agents and models";
      if (!cached && !shown) {
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
