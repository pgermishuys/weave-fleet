import { onMounted, onUnmounted, shallowRef, type Ref } from "vue";
import { animateTo } from "@/lib/phone/animate";
import { PULL_THRESHOLD, clamp, pullDistance } from "@/lib/phone/gestures";
import { iosWebKit, phoneLook } from "@/composables/phone/use-phone-env";

/**
 * Pull to refresh, as each phone does it: on iOS the spokes fill in as you pull and spin while it refreshes; on
 * Android a round indicator drops in. iPhone Safari already rubber-bands the list, so there it reads that (a negative
 * scroll); elsewhere it follows the touches itself.
 */
export function usePullToRefresh(options: {
  scroller: Ref<HTMLElement | null>;
  inner: Ref<HTMLElement | null>;
  indicator: () => HTMLElement | null;
  onRefresh: () => Promise<void>;
  stretch?: (pull: number) => void;
}) {
  const refreshing = shallowRef(false);
  const spokes = shallowRef(0);
  let armed = false;
  let pull = 0;

  const ios = (): boolean => phoneLook.value === "ios";

  function show(distance: number): void {
    pull = distance;
    const indicator = options.indicator();
    if (!indicator) return;
    if (ios()) {
      spokes.value = refreshing.value ? 8 : Math.round(clamp(distance / PULL_THRESHOLD, 0, 1) * 8);
      indicator.style.opacity = String(refreshing.value ? 1 : clamp(distance / 30, 0, 1));
      indicator.style.transform = refreshing.value ? "" : `translateY(${Math.max(0, distance - 50) * 0.5}px)`;
    } else {
      indicator.style.transform = `translateY(${clamp(distance, 0, 130) * 0.9}px) rotate(${distance * 3}deg)`;
      indicator.style.opacity = String(clamp(distance / 40, 0, 1));
    }
    if (distance > PULL_THRESHOLD && !armed) armed = true;
    if (distance < PULL_THRESHOLD - 10) armed = false;
  }

  async function trigger(): Promise<void> {
    const indicator = options.indicator();
    const inner = options.inner.value;
    refreshing.value = true;
    show(PULL_THRESHOLD);
    if (ios() && inner) await animateTo(inner, { transform: "translateY(54px)" }, 260, "var(--ph-ease-out)");
    else if (indicator) indicator.style.transform = "translateY(70px)";
    try {
      await options.onRefresh();
    } finally {
      if (ios() && inner) void animateTo(inner, { transform: "translateY(0)" }, 380);
      else if (indicator) await animateTo(indicator, { transform: "translateY(0) scale(0.2)", opacity: "0" }, 260);
      refreshing.value = false;
      armed = false;
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
        if (ios() && options.inner.value) options.inner.value.style.transform = "";
      }
      active = false;
      return;
    }
    active = true;
    event.preventDefault();
    const distance = pullDistance(dy);
    show(distance);
    if (ios()) {
      if (options.inner.value) options.inner.value.style.transform = `translateY(${distance}px)`;
      options.stretch?.(distance);
    }
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
    options.stretch?.(0);
    if (pull > PULL_THRESHOLD) {
      void trigger();
      return;
    }
    const indicator = options.indicator();
    if (ios() && options.inner.value) void animateTo(options.inner.value, { transform: "translateY(0)" }, 300);
    else if (indicator) void animateTo(indicator, { transform: "translateY(0)", opacity: "0" }, 200);
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

  return { refreshing, spokes };
}
