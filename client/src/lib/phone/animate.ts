/** Small DOM animation helpers for the phone's hand-driven motion (sheets, pushes, swipes). */

export const EASE = "cubic-bezier(0.32, 0.72, 0, 1)";
export const EASE_OUT = "cubic-bezier(0.2, 0.9, 0.3, 1)";

export function reducedMotion(): boolean {
  return typeof window !== "undefined" && window.matchMedia?.("(prefers-reduced-motion: reduce)").matches === true;
}

/** Transitions `styles` onto `el` over `ms`, resolving when done. Reduced motion keeps it to 120 ms. */
export function animateTo(el: HTMLElement, styles: Partial<Record<string, string>>, ms: number, ease = EASE): Promise<void> {
  return new Promise((resolve) => {
    const duration = reducedMotion() ? Math.min(ms, 120) : ms;
    el.style.transition = Object.keys(styles).map((key) => `${key.replace(/[A-Z]/g, (m) => `-${m.toLowerCase()}`)} ${duration}ms ${ease}`).join(",");
    void el.offsetWidth;
    Object.assign(el.style, styles);
    setTimeout(() => {
      el.style.transition = "";
      resolve();
    }, duration + 20);
  });
}

/** Folds an element away (height, opacity and spacing to zero), as a card leaving a list. */
export async function collapse(el: HTMLElement, ms = 320): Promise<void> {
  el.style.overflow = "hidden";
  el.style.height = `${el.offsetHeight}px`;
  await animateTo(el, { height: "0px", opacity: "0", marginTop: "0px", marginBottom: "0px", paddingTop: "0px", paddingBottom: "0px" }, ms);
}

/** A CSS length (a custom property such as --ph-safe-top, which may be an env()) resolved to px. */
export function cssPx(value: string): number {
  if (typeof document === "undefined") return 0;
  const probe = document.createElement("div");
  probe.style.cssText = `position:absolute;visibility:hidden;pointer-events:none;width:0;height:${value}`;
  (document.querySelector(".ph-app") ?? document.body).appendChild(probe);
  const px = probe.offsetHeight;
  probe.remove();
  return px;
}
