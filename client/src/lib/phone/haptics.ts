/**
 * A small tap you can feel, for a few confirmations only (Allow once, Start, Archive). Android: the Vibration API,
 * once the page has had a user gesture. iPhone has no API; Safari 18+ ticks when a native `<input switch>` toggles, so
 * a hidden one is clicked from inside the user's tap. That is a trick, not an API, and may stop working. Elsewhere,
 * nothing.
 */
import { isIosWebKit, readPlatformSignals } from "@/lib/phone/platform";

export type HapticKind = "light" | "success" | "heavy";

/** The vibration for each kind, in ms (Android). */
export function vibrationPattern(kind: HapticKind): number | number[] {
  if (kind === "success") return [12, 60, 18];
  if (kind === "heavy") return 22;
  return 9;
}

let iosSwitch: HTMLLabelElement | null = null;

export function haptic(kind: HapticKind = "light"): void {
  if (typeof navigator === "undefined" || typeof document === "undefined") return;
  const nav = navigator as Navigator & { userActivation?: { hasBeenActive: boolean } };
  if (typeof nav.vibrate === "function") {
    // Chrome ignores (and warns about) vibrate before the user has touched the page.
    if (nav.userActivation && !nav.userActivation.hasBeenActive) return;
    try {
      nav.vibrate(vibrationPattern(kind));
    } catch {
      // Not allowed here: nothing to feel.
    }
    return;
  }
  if (!isIosWebKit(readPlatformSignals())) return;
  if (!iosSwitch?.isConnected) {
    iosSwitch = document.createElement("label");
    iosSwitch.setAttribute("aria-hidden", "true");
    iosSwitch.style.cssText = "position:fixed;left:-100px;top:0;opacity:0;pointer-events:none";
    const input = document.createElement("input");
    input.type = "checkbox";
    input.setAttribute("switch", "");
    input.tabIndex = -1;
    iosSwitch.appendChild(input);
    document.body.appendChild(iosSwitch);
  }
  iosSwitch.click();
}
