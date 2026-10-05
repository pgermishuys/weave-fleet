import { onUnmounted, type Ref } from "vue";
import { animateTo } from "@/lib/phone/animate";
import { addSample, axisIntent, swipeBackRelease, velocity, type Sample } from "@/lib/phone/gestures";
import { iosWebKit, phoneLook, standalone } from "@/composables/phone/use-phone-env";

/** How far the page under a pushed one sits to the left (iOS parallax), and how dim it is. */
export const PARALLAX = -0.3;
export const UNDER_DIM = 0.14;

function scrollsSideways(target: Element, stop: Element): boolean {
  for (let el: Element | null = target; el && el !== stop; el = el.parentElement) {
    if (el.scrollWidth > el.clientWidth + 1) {
      const overflow = getComputedStyle(el).overflowX;
      if (overflow === "auto" || overflow === "scroll") return true;
    }
  }
  return false;
}

/**
 * Swipe back from anywhere on a pushed screen (iOS 26), on the iOS look only: the screen follows the finger, the one
 * under it slides back in from the left and brightens, and letting go goes back on a flick or past 45% — or springs
 * back. In Safari (not the Home Screen app) the very edge is left to Safari's own swipe.
 */
export function useSwipeBack(options: {
  screen: Ref<HTMLElement | null>;
  under: () => HTMLElement | null;
  dim: () => HTMLElement | null;
  enabled: () => boolean;
  onReveal: (revealed: boolean) => void;
  onBack: (fromX: number, velocity: number) => void;
}) {
  let startX = 0;
  let startY = 0;
  let dx = 0;
  let mode: "maybe" | "drag" | "no" | null = null;
  let samples: Sample[] = [];

  function paint(x: number): void {
    const screen = options.screen.value;
    const under = options.under();
    if (!screen) return;
    const progress = x / Math.max(1, screen.clientWidth);
    screen.style.transform = `translateX(${x}px)`;
    if (under) under.style.transform = `translateX(${PARALLAX * 100 * (1 - progress)}%)`;
    const dim = options.dim();
    if (dim) dim.style.opacity = String(UNDER_DIM * (1 - progress));
  }

  function onTouchStart(event: TouchEvent): void {
    mode = null;
    dx = 0;
    if (phoneLook.value !== "ios" || !options.enabled()) return;
    const touch = event.touches[0];
    if (!standalone && iosWebKit && touch.clientX < 22) return;
    const target = event.target as Element;
    if (target.closest("textarea, input, .ph-no-swipe-back")) return;
    const screen = options.screen.value;
    if (screen && scrollsSideways(target, screen)) return;
    startX = touch.clientX;
    startY = touch.clientY;
    mode = "maybe";
    samples = [[performance.now(), startX]];
  }

  function onTouchMove(event: TouchEvent): void {
    if (!mode || mode === "no") return;
    const touch = event.touches[0];
    if (mode === "maybe") {
      const intent = axisIntent(touch.clientX - startX, touch.clientY - startY);
      if (!intent) return;
      mode = intent === "x" && touch.clientX > startX ? "drag" : "no";
      if (mode === "no") return;
      startX = touch.clientX; // start from here, so the page doesn't jump
      options.onReveal(true);
    }
    event.preventDefault();
    dx = Math.max(0, touch.clientX - startX);
    samples = addSample(samples, [performance.now(), touch.clientX]);
    paint(dx);
  }

  function onTouchEnd(): void {
    if (mode !== "drag") {
      mode = null;
      return;
    }
    mode = null;
    const screen = options.screen.value;
    const width = screen?.clientWidth ?? window.innerWidth;
    const speed = velocity(samples);
    const release = swipeBackRelease({ dx, width, velocity: speed });
    if (release.back) {
      options.onBack(dx, Math.max(speed, 1.2));
      return;
    }
    const under = options.under();
    const dim = options.dim();
    if (screen) void animateTo(screen, { transform: "translateX(0)" }, 300);
    if (under) void animateTo(under, { transform: `translateX(${PARALLAX * 100}%)` }, 300);
    if (dim) void animateTo(dim, { opacity: String(UNDER_DIM) }, 300).then(() => options.onReveal(false));
    else options.onReveal(false);
  }

  function attach(el: HTMLElement): () => void {
    el.addEventListener("touchstart", onTouchStart, { passive: true });
    el.addEventListener("touchmove", onTouchMove, { passive: false });
    el.addEventListener("touchend", onTouchEnd);
    el.addEventListener("touchcancel", onTouchEnd);
    return () => {
      el.removeEventListener("touchstart", onTouchStart);
      el.removeEventListener("touchmove", onTouchMove);
      el.removeEventListener("touchend", onTouchEnd);
      el.removeEventListener("touchcancel", onTouchEnd);
    };
  }

  let detach: (() => void) | null = null;
  function bind(el: HTMLElement | null): void {
    detach?.();
    detach = el ? attach(el) : null;
  }
  onUnmounted(() => detach?.());

  return { bind };
}
