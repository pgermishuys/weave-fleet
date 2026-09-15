import { shallowRef, toValue, watch, type MaybeRefOrGetter, type ShallowRef } from "vue";
import type { FileDiffItem } from "@/api/client";
import { apiFetch } from "@/lib/api-client";

/** Open files are few; keep the bases of the most recent ones. */
const MAX_CACHED = 50;

// A session's baseline is taken once when it starts, so a file's base never changes for that session.
const cache = new Map<string, Promise<string | null>>();

/**
 * A changed file's contents at the session's baseline: "" for a file the session added, null when it can't
 * be read (it isn't among the changes, or the request failed; failures aren't cached).
 */
export function fetchDiffBase(sessionId: string, path: string): Promise<string | null> {
  const key = `${sessionId}\u0000${path}`;
  const cached = cache.get(key);
  if (cached) {
    cache.delete(key);
    cache.set(key, cached);
    return cached;
  }

  const request = (async () => {
    try {
      const response = await apiFetch(
        `/api/sessions/${encodeURIComponent(sessionId)}/diffs/file?path=${encodeURIComponent(path)}`,
      );
      if (!response.ok) return null;
      const item = (await response.json()) as FileDiffItem;
      return typeof item.before === "string" ? item.before : "";
    } catch {
      return null;
    }
  })();

  cache.set(key, request);
  void request.then((base) => {
    if (base === null && cache.get(key) === request) cache.delete(key);
  });
  while (cache.size > MAX_CACHED) {
    const oldest = cache.keys().next().value;
    if (oldest === undefined) break;
    cache.delete(oldest);
  }
  return request;
}

/** Forgets every cached base; for tests. */
export function clearDiffBaseCache(): void {
  cache.clear();
}

/** The base of a file while it's among the session's changes; null otherwise, or until it has loaded. */
export function useDiffBase(
  sessionId: MaybeRefOrGetter<string>,
  path: MaybeRefOrGetter<string>,
  changed: MaybeRefOrGetter<boolean>,
): Readonly<ShallowRef<string | null>> {
  const base = shallowRef<string | null>(null);
  let request = 0;

  watch(
    () => [toValue(sessionId), toValue(path), toValue(changed)] as const,
    async ([id, file, isChanged]) => {
      const current = ++request;
      if (!id || !file || !isChanged) {
        base.value = null;
        return;
      }
      const loaded = await fetchDiffBase(id, file);
      if (current === request) base.value = loaded;
    },
    { immediate: true },
  );

  return base;
}
