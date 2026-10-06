import { onMounted, onUnmounted, shallowRef, type Ref } from "vue";
import { animateTo } from "@/lib/phone/animate";
import { PULL_THRESHOLD, clamp, pullDistance, pullDots } from "@/lib/phone/gestures";
import { iosWebKit } from "@/composables/phone/use-phone-env";

/**
 * Pull to refresh, the same on every phone: Fleet's Working glyph fills in dot by dot as you pull, and ticks while it
 * refreshes with the list held a little lower. iPhone Safari already rubber-bands the list, so there it reads that (a
 * negative scroll); elsewhere it follows the touches itself.
 */
export function usePullToRefresh(options: {
  scroller: Ref<HTMLElement | null>;
  inner: Ref<HTMLElement | null>;
  indicator: () => HTMLElement | null;
  onRefresh: () => Promise<void>;
}) {
  const refreshing = shallowRef(false);
  const dots = shallowRef(0);
  let pull = 0;

  function show(distance: number): void {
    pull = distance;
    const indicator = options.indicator();
    if (!indicator) return;
    dots.value = pullDots(distance, refreshing.value);
    indicator.style.opacity = String(refreshing.value ? 1 : clamp(distance / 24, 0, 1));
    indicator.style.transform = refreshing.value ? "" : `translateY(${Math.max(0, distance - 56) * 0.5}px)`;
  }

  async function trigger(): Promise<void> {
    const indicator = options.indicator();
    const inner = options.inner.value;
    refreshing.value = true;
    show(PULL_THRESHOLD);
    if (inner) await animateTo(inner, { transform: "translateY(52px)" }, 260, "var(--ph-ease-out)");
    try {
      await options.onRefresh();
    } finally {
      if (inner) void animateTo(inner, { transform: "translateY(0)" }, 380);
      refreshing.value = false;
      show(0);
      if (indicator) indicator.style.opacity = "0";
    }
  }

  let startY = 0;
  let tracking = false;
  let active = false;

  function onScroll(): void {
    const el = options.scroller.value;
    if (!el || refreshing.value || el.scrollTop > 0) return;
    show(-el.scrollTop);
  }
  function onTouchStart(event: TouchEvent): void {
    const el = options.scroller.value;
    if (!el || refreshing.value) return;
    tracking = el.scrollTop <= 0;
    active = false;
    startY = event.touches[0].clientY;
  }
  function onTouchMove(event: TouchEvent): void {
    const el = options.scroller.value;
    if (!el || !tracking || refreshing.value) return;
    const dy = event.touches[0].clientY - startY;
    if (dy <= 0 || el.scrollTop > 0) {
      if (active) {
        show(0);
        if (options.inner.value) options.inner.value.style.transform = "";
      }
      active = false;
      return;
    }
    active = true;
    event.preventDefault();
    const distance = pullDistance(dy);
    show(distance);
    if (options.inner.value) options.inner.value.style.transform = `translateY(${distance}px)`;
  }
  function onTouchEnd(): void {
    const el = options.scroller.value;
    if (iosWebKit) {
      if (el && !refreshing.value && -el.scrollTop > PULL_THRESHOLD) void trigger();
      return;
    }
    if (!active) return;
    active = false;
    tracking = false;
    if (pull > PULL_THRESHOLD) {
      void trigger();
      return;
    }
    if (options.inner.value) void animateTo(options.inner.value, { transform: "translateY(0)" }, 300);
    show(0);
  }

  onMounted(() => {
    const el = options.scroller.value;
    if (!el) return;
    if (iosWebKit) el.addEventListener("scroll", onScroll, { passive: true });
    else {
      el.addEventListener("touchstart", onTouchStart, { passive: true });
      el.addEventListener("touchmove", onTouchMove, { passive: false });
    }
    el.addEventListener("touchend", onTouchEnd, { passive: true });
  });
  onUnmounted(() => {
    const el = options.scroller.value;
    el?.removeEventListener("scroll", onScroll);
    el?.removeEventListener("touchstart", onTouchStart);
    el?.removeEventListener("touchmove", onTouchMove);
    el?.removeEventListener("touchend", onTouchEnd);
  });

  return { refreshing, dots };
}
