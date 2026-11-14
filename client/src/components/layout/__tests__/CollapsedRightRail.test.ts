import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { mount } from "@vue/test-utils";
import { describe, expect, it } from "vitest";
import CollapsedRightRail from "../CollapsedRightRail.vue";

describe("CollapsedRightRail", () => {
  it("shows the chevron, the ring and the count of steps left when the session has steps", () => {
    const wrapper = mount(CollapsedRightRail, { props: { done: 3, total: 7 } });

    expect(wrapper.find(".collapsed-rail__chevron").exists()).toBe(true);
    expect(wrapper.find(".collapsed-rail__progress-count").text()).toBe("7");
    expect(wrapper.find(".collapsed-rail__label").text()).toBe("4");
  });

  it("leaves the ring out when there are no steps", () => {
    const wrapper = mount(CollapsedRightRail, { props: { done: 0, total: 0 } });

    expect(wrapper.find(".collapsed-rail__chevron").exists()).toBe(true);
    expect(wrapper.find(".collapsed-rail__progress").exists()).toBe(false);
    expect(wrapper.find(".collapsed-rail__label").text()).toBe("0");
  });

  it("asks to expand when the rail is clicked", async () => {
    const wrapper = mount(CollapsedRightRail, { props: { done: 1, total: 2 } });

    await wrapper.find("button").trigger("click");

    expect(wrapper.emitted("expand")).toHaveLength(1);
  });

  // jsdom has no layout, so guard the rule itself: with size="toolbar"'s 28px box and the button's centring,
  // the content overflowed above the rail and the sheet clipped the chevron.
  it("makes the button fill the rail and stack from the top", () => {
    const source = readFileSync(resolve(__dirname, "../CollapsedRightRail.vue"), "utf8");
    const rule = source.match(/\.collapsed-rail__button\s*\{([^}]*)\}/)?.[1] ?? "";

    expect(rule).toMatch(/height:\s*auto/);
    expect(rule).toMatch(/align-self:\s*stretch/);
    expect(rule).toMatch(/justify-content:\s*flex-start/);
  });
});
