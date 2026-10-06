/**
 * Sheets take a history entry while open, so Android's back gesture (and the browser's Back) closes the sheet instead
 * of leaving the page under it. Closing one from the page goes back past its entry; anything that navigates right
 * after closing a sheet waits for that first (`sheetHistorySettled`), or the late "back" would undo the navigation.
 */
let seq = 0;
let pendingBacks = 0;
let waiters: (() => void)[] = [];

function release(): void {
  if (pendingBacks > 0) return;
  const done = waiters;
  waiters = [];
  done.forEach((resolve) => resolve());
}

if (typeof window !== "undefined") {
  window.addEventListener("popstate", () => {
    if (pendingBacks > 0) {
      pendingBacks--;
      release();
    }
  });
}

/** The sheet entry on top of history right now (0: none). */
export function topSheetEntry(): number {
  const state = window.history.state as { phSheet?: number } | null;
  return typeof state?.phSheet === "number" ? state.phSheet : 0;
}

/** Adds an entry for a sheet that just opened, keeping the router's state, and returns its id. */
export function pushSheetEntry(): number {
  seq += 1;
  window.history.pushState({ ...(window.history.state ?? {}), phSheet: seq }, "");
  return seq;
}

/** Goes back past a sheet's entry, when it's the one on top. */
export function popSheetEntry(id: number): void {
  if (topSheetEntry() !== id) return;
  pendingBacks++;
  window.history.back();
  // A back that never reports (a browser quirk) mustn't hold navigation forever.
  setTimeout(() => {
    if (pendingBacks > 0) {
      pendingBacks = 0;
      release();
    }
  }, 600);
}

/** Resolves once no sheet is still going back past its entry. */
export function sheetHistorySettled(): Promise<void> {
  if (pendingBacks === 0) return Promise.resolve();
  return new Promise((resolve) => waiters.push(resolve));
}
