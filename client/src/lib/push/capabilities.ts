/**
 * Whether this browser can get Fleet's notifications, as one state the setup screen explains. No Vue: a native
 * wrapper would answer the same question its own way.
 */

export type PushState =
  /** Plain http on another device: Fleet works in the browser, but install and notifications need https. */
  | "insecure"
  /** iPhone or iPad in Safari: only an app added to the Home Screen can get notifications. */
  | "ios-needs-install"
  /** Everything's there; notifications can be turned on (or already are). */
  | "ready"
  /** The user said no; only the browser's settings can undo that. */
  | "denied"
  /** This browser has no Web Push. */
  | "unsupported";

export interface PushEnvironment {
  isSecureContext: boolean;
  hasServiceWorker: boolean;
  hasPushManager: boolean;
  hasNotification: boolean;
  permission: NotificationPermission | "unsupported";
  isIos: boolean;
  isStandalone: boolean;
}

export function pushState(env: PushEnvironment): PushState {
  if (!env.isSecureContext) return "insecure";
  if (env.isIos && !env.isStandalone) return "ios-needs-install";
  if (!env.hasServiceWorker || !env.hasPushManager || !env.hasNotification) return "unsupported";
  if (env.permission === "denied") return "denied";
  return "ready";
}

/** iPhone, iPad (which says it's a Mac but has touch) or iPod. */
export function isIos(userAgent: string, maxTouchPoints: number): boolean {
  return /iPhone|iPad|iPod/.test(userAgent) || (/Macintosh/.test(userAgent) && maxTouchPoints > 1);
}

/** The browser this page runs in, read for {@link pushState}. */
export function readPushEnvironment(): PushEnvironment {
  const hasWindow = typeof window !== "undefined";
  const nav = typeof navigator === "undefined" ? null : navigator;
  const standaloneQuery = hasWindow && typeof window.matchMedia === "function"
    && window.matchMedia("(display-mode: standalone)").matches;
  const iosStandalone = !!(nav as (Navigator & { standalone?: boolean }) | null)?.standalone;
  return {
    isSecureContext: hasWindow && window.isSecureContext,
    hasServiceWorker: !!nav && "serviceWorker" in nav,
    hasPushManager: hasWindow && "PushManager" in window,
    hasNotification: hasWindow && "Notification" in window,
    permission: hasWindow && "Notification" in window ? Notification.permission : "unsupported",
    isIos: !!nav && isIos(nav.userAgent, nav.maxTouchPoints ?? 0),
    isStandalone: standaloneQuery || iosStandalone,
  };
}
