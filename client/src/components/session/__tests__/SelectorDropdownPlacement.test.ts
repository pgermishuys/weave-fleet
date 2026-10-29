import { mount } from "@vue/test-utils";
import { afterEach, describe, expect, it, vi } from "vitest";
import SelectorDropdown from "@/components/session/SelectorDropdown.vue";

const items = [{ id: "a", label: "A" }, { id: "b", label: "B" }];

function rect(top: number, bottom: number): DOMRect {
  return { top, bottom, left: 0, right: 100, width: 100, height: bottom - top, x: 0, y: top, toJSON: () => ({}) };
}

/** A selector inside a dialog that spans 100..700 px, with the selector's own box at `top`. */
async function openIn(top: number, props: Record<string, unknown> = {}) {
  const dialog = document.createElement("div");
  dialog.setAttribute("role", "dialog");
  document.body.appendChild(dialog);
  vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockImplementation(function (this: HTMLElement) {
    if (this.getAttribute("role") === "dialog") return rect(100, 700);
    if (this.classList.contains("selector-dropdown-root")) return rect(top, top + 28);
    return rect(0, 0);
  });
  const wrapper = mount(SelectorDropdown, {
    attachTo: dialog,
    props: { modelValue: "a", items, label: "Test selector", ...props },
  });
  await wrapper.get("button").trigger("click");
  return wrapper;
}

afterEach(() => {
  vi.restoreAllMocks();
  document.body.innerHTML = "";
});

describe("SelectorDropdown placement", () => {
  it("opens downward, inside the dialog, when auto and there is no room above", async () => {
    const wrapper = await openIn(130, { placement: "auto" });
    const menu = wrapper.get(".selector-dropdown");
    expect(menu.classes()).toContain("selector-dropdown--down");
    // 700 - 158 - 6 gap - 8 margin is more than the menu needs, so the usual cap stands.
    expect(parseFloat((menu.element as HTMLElement).style.maxHeight)).toBeLessThanOrEqual(320);
    wrapper.unmount();
  });

  it("stays upward when auto and there is room above", async () => {
    const wrapper = await openIn(560, { placement: "auto" });
    // 560 is 460 below the top of the dialog, more than the menu's height.
    expect(wrapper.get(".selector-dropdown").classes()).not.toContain("selector-dropdown--down");
    wrapper.unmount();
  });

  it("opens upward, capped to the room above, when auto and the selector is near the bottom", async () => {
    const wrapper = await openIn(660, { placement: "auto" });
    const menu = wrapper.get(".selector-dropdown");
    expect(menu.classes()).not.toContain("selector-dropdown--down");
    expect(parseFloat((menu.element as HTMLElement).style.maxHeight)).toBeLessThanOrEqual(320);
    wrapper.unmount();
  });

  it("opens upward with no inline cap by default, wherever the selector is", async () => {
    const wrapper = await openIn(130);
    const menu = wrapper.get(".selector-dropdown");
    expect(menu.classes()).not.toContain("selector-dropdown--down");
    expect(menu.attributes("style")).toBeUndefined();
    wrapper.unmount();
  });
});

describe("SelectorDropdown Escape", () => {
  it("leaves Escape alone while closed, so a dialog around it can take it", async () => {
    const wrapper = mount(SelectorDropdown, { attachTo: document.body, props: { modelValue: "a", items, label: "Test selector" } });
    const event = new KeyboardEvent("keydown", { key: "Escape", bubbles: true, cancelable: true });
    wrapper.get("button").element.dispatchEvent(event);
    expect(event.defaultPrevented).toBe(false);
    wrapper.unmount();
  });

  it("takes Escape and closes while open", async () => {
    const wrapper = mount(SelectorDropdown, { attachTo: document.body, props: { modelValue: "a", items, label: "Test selector" } });
    await wrapper.get("button").trigger("click");
    const event = new KeyboardEvent("keydown", { key: "Escape", bubbles: true, cancelable: true });
    wrapper.get("input").element.dispatchEvent(event);
    await wrapper.vm.$nextTick();
    expect(event.defaultPrevented).toBe(true);
    expect(wrapper.find(".selector-dropdown").exists()).toBe(false);
    wrapper.unmount();
  });
});
