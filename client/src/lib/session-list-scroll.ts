/**
 * Where the sessions list was scrolled when a click on another machine's session reloaded the page, so the list can
 * come back to the same place and the row clicked stays under the pointer. Per tab (sessionStorage), and only for a
 * moment: a saved place that's older than the reload could be is ignored.
 */
const SCROLL_KEY = "weave:sessions-list-scroll";
const MAX_AGE_MS = 30_000;

export function saveSessionListScroll(top: number): void {
  try {
    sessionStorage.setItem(SCROLL_KEY, JSON.stringify({ top, at: Date.now() }));
  } catch {
    // Without storage the list just starts at the top.
  }
}

/** The saved place, once: reading it clears it. Null when there's none, or it's stale. */
export function takeSessionListScroll(): number | null {
  try {
    const raw = sessionStorage.getItem(SCROLL_KEY);
    if (raw === null) return null;
    sessionStorage.removeItem(SCROLL_KEY);
    const saved = JSON.parse(raw) as { top?: unknown; at?: unknown };
    if (typeof saved.top !== "number" || typeof saved.at !== "number") return null;
    return Date.now() - saved.at <= MAX_AGE_MS ? saved.top : null;
  } catch {
    return null;
  }
}
