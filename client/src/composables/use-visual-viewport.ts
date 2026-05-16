import { onMounted, onUnmounted } from "vue";

let listenerCount = 0;

function setVisualVh(): void {
  if (typeof window === "undefined") {
    return;
  }

  const vvh = window.visualViewport?.height ?? window.innerHeight;
  const wih = window.innerHeight;
  const vh = Math.min(vvh, wih);

  document.documentElement.style.setProperty("--visual-vh", `${vh}px`);
}

function onResize(): void {
  setVisualVh();
}

function attach(): void {
  window.addEventListener("resize", onResize);

  if (window.visualViewport) {
    window.visualViewport.addEventListener("resize", onResize);
    window.visualViewport.addEventListener("scroll", onResize);
  }
}

function detach(): void {
  window.removeEventListener("resize", onResize);

  if (window.visualViewport) {
    window.visualViewport.removeEventListener("resize", onResize);
    window.visualViewport.removeEventListener("scroll", onResize);
  }
}

/**
 * Tracks the visual viewport height (shrinks when the on-screen keyboard opens)
 * and writes it to the CSS custom property `--visual-vh` on the `<html>` element.
 *
 * Use `height: var(--visual-vh, 100dvh)` in CSS to respect the keyboard.
 */
export function useVisualViewport(): void {
  onMounted(() => {
    if (typeof window === "undefined") {
      return;
    }

    listenerCount++;

    if (listenerCount === 1) {
      attach();
    }

    setVisualVh();
  });

  onUnmounted(() => {
    listenerCount--;

    if (listenerCount === 0) {
      detach();
    }
  });
}
