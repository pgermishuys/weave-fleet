/**
 * JS hooks for <Transition :css="false"> / <TransitionGroup :css="false"> that
 * open and close an element at its measured height, so the content below moves
 * exactly as far as the element grows or shrinks. Honours reduced motion.
 *
 *   <Transition :css="false" @enter="heightEnter" @leave="heightLeave">
 */

const DURATION_MS = 160;
const EASE_OUT = "cubic-bezier(0.2, 0.8, 0.2, 1)";
const EASE_IN = "cubic-bezier(0.4, 0, 1, 1)";

function prefersReducedMotion(): boolean {
  return typeof window !== "undefined"
    && typeof window.matchMedia === "function"
    && window.matchMedia("(prefers-reduced-motion: reduce)").matches;
}

function canAnimate(el: Element): el is HTMLElement {
  return el instanceof HTMLElement && typeof el.animate === "function" && !prefersReducedMotion();
}

export function heightEnter(el: Element, done: () => void): void {
  if (!canAnimate(el)) {
    done();
    return;
  }

  const height = el.scrollHeight;
  el.style.overflow = "hidden";
  const animation = el.animate(
    [{ height: "0px", opacity: 0 }, { height: `${height}px`, opacity: 1 }],
    { duration: DURATION_MS, easing: EASE_OUT },
  );
  animation.onfinish = () => {
    el.style.overflow = "";
    done();
  };
  animation.oncancel = () => {
    el.style.overflow = "";
    done();
  };
}

export function heightLeave(el: Element, done: () => void): void {
  if (!canAnimate(el)) {
    done();
    return;
  }

  const height = el.offsetHeight;
  el.style.overflow = "hidden";
  el.style.pointerEvents = "none";
  const animation = el.animate(
    [{ height: `${height}px`, opacity: 1 }, { height: "0px", opacity: 0 }],
    { duration: DURATION_MS - 20, easing: EASE_IN, fill: "forwards" },
  );
  animation.onfinish = done;
  animation.oncancel = done;
}
