"use client";

import { useSyncExternalStore, useCallback } from "react";

// ── Per-query subscriber registry ────────────────────────────────────────────
const queryListeners = new Map<string, Set<() => void>>();
const queryLists = new Map<string, MediaQueryList>();

function getMediaQueryList(query: string): MediaQueryList | null {
  if (typeof window === "undefined" || typeof window.matchMedia !== "function") return null;
  let mql = queryLists.get(query);
  if (!mql) {
    mql = window.matchMedia(query);
    queryLists.set(query, mql);
  }
  return mql;
}

function subscribeToQuery(query: string) {
  return (callback: () => void) => {
    let listeners = queryListeners.get(query);
    if (!listeners) {
      listeners = new Set();
      queryListeners.set(query, listeners);
    }
    listeners.add(callback);

    const mql = getMediaQueryList(query);
    const handler = () => {
      const ls = queryListeners.get(query);
      if (ls) {
        for (const listener of ls) {
          listener();
        }
      }
    };
    mql?.addEventListener("change", handler);

    return () => {
      listeners!.delete(callback);
      if (listeners!.size === 0) {
        queryListeners.delete(query);
        mql?.removeEventListener("change", handler);
      }
    };
  };
}

// ── Core hook ─────────────────────────────────────────────────────────────────

/**
 * SSR-safe hook that returns whether a CSS media query matches.
 * Returns false during SSR; hydrates correctly on mount.
 * Uses useSyncExternalStore for concurrent-safe React 19 pattern.
 */
export function useMediaQuery(query: string): boolean {
  // eslint-disable-next-line react-hooks/exhaustive-deps
  const subscribe = useCallback((cb: () => void) => subscribeToQuery(query)(cb), [query]);

  const getSnapshot = useCallback(() => {
    return getMediaQueryList(query)?.matches ?? false;
  }, [query]);

  // Server snapshot always returns false — ensures hydration match
  const getServerSnapshot = useCallback(() => false, []);

  return useSyncExternalStore(subscribe, getSnapshot, getServerSnapshot);
}

// ── Convenience hooks ─────────────────────────────────────────────────────────

/** True when viewport is < 768px (phone portrait and small phone landscape) */
export function useIsMobile(): boolean {
  return useMediaQuery("(max-width: 767px)");
}

/**
 * True when the nav should render in mobile mode (drawer + bottom bar).
 * Uses the fold breakpoint (717px) so that foldable devices in unfolded posture
 * switch to the inline sidebar / tablet nav pattern rather than the drawer.
 */
export function useIsMobileNav(): boolean {
  return useMediaQuery("(max-width: 716px)");
}

/** True when viewport is 768px–1023px (tablet) */
export function useIsTablet(): boolean {
  return useMediaQuery("(min-width: 768px) and (max-width: 1023px)");
}

/** True when viewport is >= 1024px (laptop / desktop) */
export function useIsDesktop(): boolean {
  return useMediaQuery("(min-width: 1024px)");
}
