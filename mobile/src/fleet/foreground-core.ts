// Which AppState changes are the app really going to the background or coming back to the front.

/**
 * The new foreground state for an AppState value, or null when nothing changed. iOS reports "inactive" for Control
 * Center, the notification pull-down, the app switcher and permission alerts, and then "active" again: the app never
 * left the front, so that bounce must not read as a return from the background (which refetches every machine).
 */
export function foregroundChange(foreground: boolean, state: string): boolean | null {
  if (state === "background" && foreground) return false;
  if (state === "active" && !foreground) return true;
  return null;
}
