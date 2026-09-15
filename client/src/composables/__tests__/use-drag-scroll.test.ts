import { afterEach, describe, expect, it, vi } from "vitest";
import { effectScope, nextTick, ref } from "vue";
import { useDragScroll } from "@/composables/use-drag-scroll";

function strip(scrollWidth: number, clientWidth: number): HTMLElement {
  const el = document.createElement("div");
  Object.defineProperty(el, "scrollWidth", { value: scrollWidth });
  Object.defineProperty(el, "clientWidth", { value: clientWidth });
  document.body.append(el);
  return el;
}

function pointer(el: HTMLElement, type: string, clientX: number, extra: Record<string, unknown> = {}): void {
  const event = new MouseEvent(type, { bubbles: true, cancelable: true, clientX, button: 0 });
  Object.assign(event, { pointerId: 1, pointerType: "mouse", ...extra });
  el.dispatchEvent(event);
}

describe("useDragScroll", () => {
  const scope = effectScope();

  afterEach(() => {
    document.body.innerHTML = "";
  });

  async function setup(el: HTMLElement) {
    const target = ref<HTMLElement | null>(el);
    const result = scope.run(() => useDragScroll(target))!;
    await nextTick();
    return result;
  }

  it("scrolls the strip by the distance the mouse is dragged", async () => {
    const el = strip(800, 300);
    el.scrollLeft = 100;
    const { dragging } = await setup(el);

    pointer(el, "pointerdown", 200);
    pointer(el, "pointermove", 150);

    expect(el.scrollLeft).toBe(150);
    expect(dragging.value).toBe(true);

    pointer(el, "pointerup", 150);
    expect(dragging.value).toBe(false);
  });

  it("swallows the click that ends a drag, but not a plain click", async () => {
    const el = strip(800, 300);
    const tab = document.createElement("button");
    el.append(tab);
    const clicked = vi.fn();
    tab.addEventListener("click", clicked);
    await setup(el);

    pointer(tab, "pointerdown", 200);
    pointer(tab, "pointermove", 120);
    pointer(tab, "pointerup", 120);
    tab.click();
    expect(clicked).not.toHaveBeenCalled();

    pointer(tab, "pointerdown", 200);
    pointer(tab, "pointerup", 201);
    tab.click();
    expect(clicked).toHaveBeenCalledTimes(1);
  });

  it("leaves touch to native scrolling and ignores strips that fit", async () => {
    const touchStrip = strip(800, 300);
    await setup(touchStrip);
    pointer(touchStrip, "pointerdown", 200, { pointerType: "touch" });
    pointer(touchStrip, "pointermove", 100, { pointerType: "touch" });
    expect(touchStrip.scrollLeft).toBe(0);

    const fits = strip(300, 300);
    await setup(fits);
    pointer(fits, "pointerdown", 200);
    pointer(fits, "pointermove", 100);
    expect(fits.scrollLeft).toBe(0);
  });

  it("turns a vertical wheel into sideways scrolling", async () => {
    const el = strip(800, 300);
    await setup(el);

    const wheel = new WheelEvent("wheel", { deltaY: 60, cancelable: true });
    el.dispatchEvent(wheel);

    expect(el.scrollLeft).toBe(60);
    expect(wheel.defaultPrevented).toBe(true);
  });
});
