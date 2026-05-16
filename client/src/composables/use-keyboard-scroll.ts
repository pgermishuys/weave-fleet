import { onMounted, onUnmounted } from "vue";

let debounceTimer: ReturnType<typeof setTimeout> | undefined;

function scrollActiveInputIntoView(): void {
  const el = document.activeElement;

  if (
    el instanceof HTMLInputElement
    || el instanceof HTMLTextAreaElement
  ) {
    clearTimeout(debounceTimer);
    debounceTimer = setTimeout(() => {
      el.scrollIntoView({ block: "nearest", behavior: "smooth" });
    }, 50);
  }
}

/**
 * When the virtual keyboard opens (visualViewport shrinks), scrolls the
 * currently focused input/textarea element into view so it isn't obscured.
 * Only active on devices that have a visualViewport API.
 */
export function useKeyboardScroll(): void {
  onMounted(() => {
    if (typeof window === "undefined" || !window.visualViewport) {
      return;
    }

    window.visualViewport.addEventListener("resize", scrollActiveInputIntoView);
  });

  onUnmounted(() => {
    if (typeof window !== "undefined" && window.visualViewport) {
      window.visualViewport.removeEventListener("resize", scrollActiveInputIntoView);
    }

    clearTimeout(debounceTimer);
  });
}
