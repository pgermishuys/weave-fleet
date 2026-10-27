/**
 * A tab left open across a Fleet update still points at the old build's chunk files, which the new build no longer
 * has: the server answers those URLs with index.html, so opening a canvas or a route fails with "Failed to fetch
 * dynamically imported module" and nothing on screen says why. Reloading once fetches the new index.html.
 *
 * Reloads at most once per `minGapMs`, so a chunk that is really missing (a broken deploy) can't loop the page.
 */
const RELOAD_KEY = "fleet.stale-chunk-reload-at";
const DEFAULT_MIN_GAP_MS = 60_000;

/** Messages browsers give when a lazily loaded chunk can't be imported (Chrome, Firefox, Safari). */
const CHUNK_FAILURE = /Failed to fetch dynamically imported module|error loading dynamically imported module|Importing a module script failed|Unable to preload CSS/i;

export function isChunkLoadFailure(reason: unknown): boolean {
  const message = reason instanceof Error ? reason.message : typeof reason === "string" ? reason : "";
  return CHUNK_FAILURE.test(message);
}

interface Environment {
  addEventListener: (type: string, listener: (event: never) => void) => void;
  reload: () => void;
  storage: Pick<Storage, "getItem" | "setItem">;
  now: () => number;
}

/** Reloads the page unless it already did within `minGapMs`; returns whether it reloaded. */
export function reloadOnceForStaleChunk(env: Pick<Environment, "reload" | "storage" | "now">, minGapMs = DEFAULT_MIN_GAP_MS): boolean {
  try {
    const last = Number(env.storage.getItem(RELOAD_KEY));
    if (Number.isFinite(last) && last > 0 && env.now() - last < minGapMs) return false;
    env.storage.setItem(RELOAD_KEY, String(env.now()));
  } catch {
    // Storage blocked: we can't guard against a loop, so don't reload.
    return false;
  }
  env.reload();
  return true;
}

export function installStaleChunkReload(env: Environment = {
  addEventListener: (type, listener) => window.addEventListener(type, listener as EventListener),
  reload: () => window.location.reload(),
  storage: window.sessionStorage,
  now: () => Date.now(),
}): void {
  // Vite raises this when a lazy import (route or canvas) fails to load its chunk.
  env.addEventListener("vite:preloadError", ((event: Event) => {
    if (reloadOnceForStaleChunk(env)) event.preventDefault();
  }) as (event: never) => void);

  // Imports Vite doesn't wrap (a plain dynamic import) reject without that event.
  env.addEventListener("unhandledrejection", ((event: PromiseRejectionEvent) => {
    if (isChunkLoadFailure(event.reason)) reloadOnceForStaleChunk(env);
  }) as (event: never) => void);
}
