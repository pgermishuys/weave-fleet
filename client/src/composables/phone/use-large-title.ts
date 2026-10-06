import { onMounted, onUnmounted, shallowRef, type Ref } from "vue";

/**
 * The page head in the panel and the chrome bar above it: once the head ("Needs you", the machines under it) scrolls
 * away, the bar shows the page's name beside the logo. A scroll that stops halfway through the head settles to either
 * side, so the head is never left cut in two.
 */
export function useLargeTitle(scroller: Ref<HTMLElement | null>, title: Ref<HTMLElement | null>) {
  const scrolled = shallowRef(false);
  let touching = false;
  let idle: ReturnType<typeof setTimeout> | undefined;

  const threshold = (): number => (title.value?.offsetHeight ?? 60) - 8;

  function onScroll(): void {
    const el = scroller.value;
    if (!el) return;
    const y = el.scrollTop;
    scrolled.value = y > threshold() - 12;
    clearTimeout(idle);
    idle = setTimeout(() => {
      const edge = threshold();
      if (!touching && y > 0 && y < edge) el.scrollTo({ top: y < edge / 2 ? 0 : edge, behavior: "smooth" });
    }, 160);
  }
  const onTouchStart = (): void => {
    touching = true;
  };
  const onTouchEnd = (): void => {
    touching = false;
    onScroll();
  };

  onMounted(() => {
    const el = scroller.value;
    el?.addEventListener("scroll", onScroll, { passive: true });
    el?.addEventListener("touchstart", onTouchStart, { passive: true });
    el?.addEventListener("touchend", onTouchEnd, { passive: true });
  });
  onUnmounted(() => {
    clearTimeout(idle);
    const el = scroller.value;
    el?.removeEventListener("scroll", onScroll);
    el?.removeEventListener("touchstart", onTouchStart);
    el?.removeEventListener("touchend", onTouchEnd);
  });

  return { scrolled };
}
