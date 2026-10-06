/**
 * What the phone is, where the app needs to know: Safari's engine on an iPhone or iPad behaves differently for a few
 * tricks (its own edge swipe back, its rubber band for pull to refresh, the haptic switch). Fleet looks the same on
 * every phone; nothing here changes how it draws. No Vue.
 */
export interface PlatformSignals {
  userAgent: string;
  /** `navigator.platform` ("MacIntel" on an iPad that asks for desktop sites). */
  platform?: string;
  maxTouchPoints?: number;
}

/** Where the native look used to remember its choice; cleared once so it doesn't linger. */
export const RETIRED_LOOK_KEY = "weave:phone-look";

/** Safari's engine on an iPhone or iPad (every iOS browser is), where some tricks differ. */
export function isIosWebKit(signals: PlatformSignals): boolean {
  return /iP(hone|ad|od)/.test(signals.userAgent) || (signals.platform === "MacIntel" && (signals.maxTouchPoints ?? 0) > 1);
}

/** What this browser says about itself. */
export function readPlatformSignals(): PlatformSignals {
  return { userAgent: navigator.userAgent, platform: navigator.platform, maxTouchPoints: navigator.maxTouchPoints };
}
