/**
 * Which phone the app dresses up as: iOS (glass bars, large titles, a floating tab bar, swipe back) or Android
 * (Material 3: a back arrow, a flat tab bar, New session as a button). Same screens, same layout. Detected from the
 * browser; `?look=ios|android` overrides it for testing and is remembered, `?look=auto` forgets it. No Vue.
 */
export type PhoneLook = "ios" | "android";

export const LOOK_STORAGE_KEY = "weave:phone-look";

export interface LookSignals {
  userAgent: string;
  /** `navigator.platform` ("MacIntel" on an iPad that asks for desktop sites). */
  platform?: string;
  maxTouchPoints?: number;
  /** `navigator.userAgentData.platform`, where the browser has it (Chromium). */
  uaDataPlatform?: string | null;
}

/** Android when the browser says so; iOS everywhere else (it's the look the mockups were approved in). */
export function detectLook(signals: LookSignals): PhoneLook {
  const platform = signals.uaDataPlatform?.toLowerCase();
  if (platform === "android") return "android";
  if (platform === "ios") return "ios";
  return /Android/i.test(signals.userAgent) ? "android" : "ios";
}

/** Safari's engine on an iPhone or iPad (every iOS browser is), where some tricks differ. */
export function isIosWebKit(signals: LookSignals): boolean {
  return /iP(hone|ad|od)/.test(signals.userAgent) || (signals.platform === "MacIntel" && (signals.maxTouchPoints ?? 0) > 1);
}

function asLook(value: string | null | undefined): PhoneLook | null {
  return value === "ios" || value === "android" ? value : null;
}

/**
 * The look to use, and what to remember: a `?look=` in the address wins (and is stored), `auto` clears what's stored,
 * then whatever was stored, then what the browser is.
 */
export function resolveLook(input: { query: string | null; stored: string | null; signals: LookSignals }): { look: PhoneLook; store: PhoneLook | null | undefined } {
  const asked = asLook(input.query);
  if (asked) return { look: asked, store: asked };
  if (input.query === "auto") return { look: detectLook(input.signals), store: null };
  return { look: asLook(input.stored) ?? detectLook(input.signals), store: undefined };
}

/** What this browser says about itself. */
export function readLookSignals(): LookSignals {
  const nav = navigator as Navigator & { userAgentData?: { platform?: string } };
  return {
    userAgent: nav.userAgent,
    platform: nav.platform,
    maxTouchPoints: nav.maxTouchPoints,
    uaDataPlatform: nav.userAgentData?.platform ?? null,
  };
}
