import { onScopeDispose, shallowRef, watch, type Ref, type ShallowRef } from "vue";

/** How far the pointer moves before a press becomes a drag rather than a click. */
const DRAG_THRESHOLD_PX = 4;

/**
 * Lets a strip that overflows sideways, with its scrollbar hidden, be dragged with the mouse and scrolled
 * with a vertical wheel. Touch already scrolls natively, so only mouse and pen presses drag. A press that
 * turned into a drag doesn't also click what it started on.
 */
export function useDragScroll(target: Ref<HTMLElement | null>): { dragging: Readonly<ShallowRef<boolean>> } {
  const dragging = shallowRef(false);

  let pointerId: number | null = null;
  let startX = 0;
  let startScroll = 0;
  let moved = false;

  function overflows(el: HTMLElement): boolean {
    return el.scrollWidth > el.clientWidth + 1;
  }

  function onPointerDown(event: PointerEvent): void {
    moved = false;
    const el = target.value;
    if (!el || event.button !== 0 || event.pointerType === "touch" || !overflows(el)) return;
    pointerId = event.pointerId;
    startX = event.clientX;
    startScroll = el.scrollLeft;
  }

  function onPointerMove(event: PointerEvent): void {
    const el = target.value;
    if (!el || event.pointerId !== pointerId) return;
    const dx = event.clientX - startX;
    if (!moved) {
      if (Math.abs(dx) < DRAG_THRESHOLD_PX) return;
      moved = true;
      dragging.value = true;
      el.setPointerCapture?.(event.pointerId);
    }
    el.scrollLeft = startScroll - dx;
  }

  function endDrag(event: PointerEvent): void {
    if (event.pointerId !== pointerId) return;
    pointerId = null;
    dragging.value = false;
    if (target.value?.hasPointerCapture?.(event.pointerId)) target.value.releasePointerCapture(event.pointerId);
  }

  // Runs in the capture phase, before the tab under the pointer sees the click.
  function onClickCapture(event: MouseEvent): void {
    if (!moved) return;
    moved = false;
    event.preventDefault();
    event.stopPropagation();
  }

  function onWheel(event: WheelEvent): void {
    const el = target.value;
    if (!el || event.ctrlKey || !overflows(el)) return;
    // Trackpads scroll sideways on their own; turn a mouse wheel's vertical turn into a sideways one.
    if (Math.abs(event.deltaY) <= Math.abs(event.deltaX)) return;
    const before = el.scrollLeft;
    el.scrollLeft += event.deltaY;
    if (el.scrollLeft !== before) event.preventDefault();
  }

  watch(
    target,
    (el, _previous, onCleanup) => {
      if (!el) return;
      el.addEventListener("pointerdown", onPointerDown);
      el.addEventListener("pointermove", onPointerMove);
      el.addEventListener("pointerup", endDrag);
      el.addEventListener("pointercancel", endDrag);
      el.addEventListener("click", onClickCapture, true);
      el.addEventListener("wheel", onWheel, { passive: false });
      onCleanup(() => {
        el.removeEventListener("pointerdown", onPointerDown);
        el.removeEventListener("pointermove", onPointerMove);
        el.removeEventListener("pointerup", endDrag);
        el.removeEventListener("pointercancel", endDrag);
        el.removeEventListener("click", onClickCapture, true);
        el.removeEventListener("wheel", onWheel);
      });
    },
    { immediate: true, flush: "post" },
  );

  onScopeDispose(() => {
    dragging.value = false;
  });

  return { dragging };
}
