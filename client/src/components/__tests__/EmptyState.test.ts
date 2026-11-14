import { mount } from "@vue/test-utils";
import { Inbox } from "lucide-vue-next";
import { describe, expect, it } from "vitest";
import { h } from "vue";
import { EmptyState as EmptyStateComponent } from "@/components/ui/empty-state";

// CI's @vue/test-utils (2.2.7) can't type mount() of a component with named slots (TS2769).
// eslint-disable-next-line @typescript-eslint/no-explicit-any
const EmptyState = EmptyStateComponent as any;

describe("EmptyState", () => {
  it("shows the title and one line of text, and nothing else unless asked", () => {
    const wrapper = mount(EmptyState, { props: { title: "No jobs yet", description: "Post one with New job." } });
    const lines = wrapper.findAll("p");
    expect(lines.map((p) => p.text())).toEqual(["No jobs yet", "Post one with New job."]);
    expect(wrapper.find("svg").exists()).toBe(false);
    expect(wrapper.classes()).not.toContain("border-dashed");
  });

  it("draws the icon (hidden from screen readers), the dashed card and the action", () => {
    const wrapper = mount(EmptyState, {
      props: { title: "No skills installed", icon: Inbox, bordered: true },
      slots: { action: () => h("button", { type: "button" }, "Open settings") },
    });
    expect(wrapper.get("svg").attributes("aria-hidden")).toBe("true");
    expect(wrapper.classes()).toContain("border-dashed");
    expect(wrapper.get("button").text()).toBe("Open settings");
  });

  it("takes rich text in the default slot instead of the description", () => {
    const wrapper = mount(EmptyState, {
      props: { title: "Nothing to track yet" },
      slots: { default: () => ["A plan in ", h("code", ".weave/plans/")] },
    });
    expect(wrapper.get("code").text()).toBe(".weave/plans/");
  });

  it("passes test ids and classes through to its root", () => {
    const wrapper = mount(EmptyState, { props: { title: "None", class: "flex-1" }, attrs: { "data-testid": "x-empty" } });
    expect(wrapper.attributes("data-testid")).toBe("x-empty");
    expect(wrapper.classes()).toContain("flex-1");
  });
});
