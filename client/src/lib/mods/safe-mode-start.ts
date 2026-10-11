/**
 * "Start without mods" from the address (`?mods=off`). It runs before the app mounts, so a mod that breaks the page
 * can still be stopped by opening Fleet this way. Safe mode is per user and held by the server; this only asks for it.
 */

import { apiFetchOn } from "@/lib/api-client";
import { MODS_OFF_QUERY, MODS_OFF_VALUE } from "@/lib/mods/kept";

/** The request waits here when the server couldn't take it yet (not signed in, offline); the next start in this tab retries. */
export const START_WITHOUT_MODS_KEY = "fleet.mods.startWithout";
/** A slow server never holds the app back longer than this. */
export const START_WITHOUT_MODS_TIMEOUT_MS = 3000;

function storage(): Storage | null {
  try {
    return typeof sessionStorage === "undefined" ? null : sessionStorage;
  } catch {
    return null;
  }
}

/** Takes `mods=off` off the address (nothing else) and says whether it was there. */
function takeOffTheAddress(): boolean {
  if (typeof window === "undefined") return false;
  const url = new URL(window.location.href);
  const values = url.searchParams.getAll(MODS_OFF_QUERY);
  if (!values.some((value) => value.toLowerCase() === MODS_OFF_VALUE)) return false;
  url.searchParams.delete(MODS_OFF_QUERY);
  window.history.replaceState(window.history.state, "", url.pathname + url.search + url.hash);
  return true;
}

/** Sends the request kept in this tab, if any. A definite answer settles it; not signed in, offline or slow keeps it. */
export async function applyPendingStartWithoutMods(): Promise<void> {
  const store = storage();
  if (!store?.getItem(START_WITHOUT_MODS_KEY)) return;

  const abort = new AbortController();
  const timer = setTimeout(() => abort.abort(), START_WITHOUT_MODS_TIMEOUT_MS);
  try {
    // The address is the home Fleet's, so it is the home server's mods this stops, whichever machine the page restores.
    const response = await apiFetchOn(null, "/api/mods/safe-mode", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ on: true }),
      signal: abort.signal,
    });
    const notYet = response.status === 401 || response.status === 403 || response.status >= 500;
    if (!notYet) store.removeItem(START_WITHOUT_MODS_KEY);
  } catch {
    // Offline or too slow: stays kept for the next start.
  } finally {
    clearTimeout(timer);
  }
}

/** What `main.ts` awaits before mounting: notes `?mods=off`, cleans the address, and asks the server to stop the mods. */
export async function startWithoutModsFromAddress(): Promise<void> {
  if (takeOffTheAddress()) storage()?.setItem(START_WITHOUT_MODS_KEY, "1");
  await applyPendingStartWithoutMods();
}
