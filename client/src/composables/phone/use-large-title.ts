import { onMounted, onUnmounted, shallowRef, type Ref } from "vue";

/**
 * iOS large titles: the big title scrolls away into the bar, which then shows the small title over a soft edge; a
 * scroll that stops halfway snaps to either side, and pulling down past the top stretches the title a little.
 * `scrolled` drives the bar; `stretch(px)` is for pull to refresh, which moves the content itself.
 */
export function useLargeTitle(scroller: Ref<HTMLElement | null>, title: Ref<HTMLElement | null>) {
  const scrolled = shallowRef(false);
  let touching = false;
  let idle: ReturnType<typeof setTimeout> | undefined;

  const threshold = (): number => (title.value?.offsetHeight ?? 60) - 6;

  function stretch(pull: number): void {
    if (title.value) title.value.style.transform = pull > 0 ? `scale(${1 + Math.min(pull, 120) / 900})` : "";
  }

  function onScroll(): void {
    const el = scroller.value;
    if (!el) return;
    const y = el.scrollTop;
    scrolled.value = y > threshold() - 10;
    stretch(-y);
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

  return { scrolled, stretch };
}
