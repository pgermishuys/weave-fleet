"use client";

import { useSyncExternalStore, useCallback } from "react";

// ── Types ─────────────────────────────────────────────────────────────────────

interface ViewportSegment {
  top: number;
  left: number;
  bottom: number;
  right: number;
  width: number;
  height: number;
}

export interface FoldableScreenState {
  /** True when the device is in dual-screen / fold posture */
  isFolded: boolean;
  /** Width of the fold hinge gap in px (0 on non-foldable devices) */
  foldWidth: number;
  /** The two screen segments, or null when not in dual-screen mode */
  segments: [ViewportSegment, ViewportSegment] | null;
}

// ── Fold detection helpers ────────────────────────────────────────────────────

const FOLD_QUERY = "(horizontal-viewport-segments: 2)";

let foldMql: MediaQueryList | null = null;
const foldListeners = new Set<() => void>();

function getFoldMql(): MediaQueryList | null {
  if (typeof window === "undefined") return null;
  if (!foldMql) {
    foldMql = window.matchMedia(FOLD_QUERY);
    foldMql.addEventListener("change", () => {
      for (const listener of foldListeners) {
        listener();
      }
    });
  }
  return foldMql;
}

function subscribeFold(callback: () => void): () => void {
  foldListeners.add(callback);
  // Also ensure the MQL is created and wired up
  getFoldMql();
  return () => {
    foldListeners.delete(callback);
  };
}

function getSegments(): [ViewportSegment, ViewportSegment] | null {
  if (typeof window === "undefined") return null;

  // The CSS Environment Variables API for viewport segments
  // Supported in Chrome/Edge with experimental foldable APIs
  try {
    // Access via CSS.supports or check if the environment variables exist
    const vv = window.visualViewport;
    if (!vv) return null;

    const mql = getFoldMql();
    if (!mql?.matches) return null;

    // Attempt to read segment geometry from window.getWindowSegments() (old API)
    // or fall back to dividing the viewport at the midpoint
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const win = window as any;
    if (typeof win.getWindowSegments === "function") {
      const segs = win.getWindowSegments() as Array<DOMRect>;
      if (segs && segs.length === 2) {
        return [
          {
            top: segs[0].top,
            left: segs[0].left,
            bottom: segs[0].bottom,
            right: segs[0].right,
            width: segs[0].width,
            height: segs[0].height,
          },
          {
            top: segs[1].top,
            left: segs[1].left,
            bottom: segs[1].bottom,
            right: segs[1].right,
            width: segs[1].width,
            height: segs[1].height,
          },
        ];
      }
    }

    // Fallback: split at midpoint (approximation)
    const halfWidth = Math.floor(vv.width / 2);
    return [
      { top: 0, left: 0, bottom: vv.height, right: halfWidth, width: halfWidth, height: vv.height },
      { top: 0, left: halfWidth, bottom: vv.height, right: vv.width, width: halfWidth, height: vv.height },
    ];
  } catch {
    return null;
  }
}

const SERVER_STATE: FoldableScreenState = { isFolded: false, foldWidth: 0, segments: null };

// Cached snapshot — only replaced when values actually change, so
// useSyncExternalStore's Object.is comparison works correctly.
let cachedState: FoldableScreenState = SERVER_STATE;

function getFoldStateSnapshot(): FoldableScreenState {
  if (typeof window === "undefined") {
    return SERVER_STATE;
  }

  const mql = getFoldMql();
  const isFolded = mql?.matches ?? false;

  if (!isFolded) {
    if (!cachedState.isFolded && cachedState.foldWidth === 0 && cachedState.segments === null) {
      return cachedState;
    }
    cachedState = { isFolded: false, foldWidth: 0, segments: null };
    return cachedState;
  }

  const segments = getSegments();
  let foldWidth = 0;

  if (segments) {
    foldWidth = Math.max(0, segments[1].left - segments[0].right);
  }

  // Only create a new object if something changed
  if (
    cachedState.isFolded === isFolded &&
    cachedState.foldWidth === foldWidth &&
    cachedState.segments?.[0]?.width === segments?.[0]?.width &&
    cachedState.segments?.[1]?.width === segments?.[1]?.width
  ) {
    return cachedState;
  }

  cachedState = { isFolded, foldWidth, segments };
  return cachedState;
}

// ── Hook ──────────────────────────────────────────────────────────────────────

/**
 * Detects foldable device posture and returns segment geometry.
 * Falls back gracefully on non-foldable browsers (isFolded: false, segments: null).
 *
 * @example
 * const { isFolded, segments } = useFoldableScreen();
 * if (isFolded && segments) {
 *   // render dual-pane layout using segments[0].width and segments[1].width
 * }
 */
export function useFoldableScreen(): FoldableScreenState {
  // eslint-disable-next-line react-hooks/exhaustive-deps
  const subscribe = useCallback((cb: () => void) => subscribeFold(cb), []);
  // eslint-disable-next-line react-hooks/exhaustive-deps
  const getSnapshot = useCallback(() => getFoldStateSnapshot(), []);
  const getServerSnapshot = useCallback(() => SERVER_STATE, []);

  return useSyncExternalStore(subscribe, getSnapshot, getServerSnapshot);
}
