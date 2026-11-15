import { mount } from "@vue/test-utils";
import { describe, expect, it, vi } from "vitest";
import { h } from "vue";
import { PageHeader as PageHeaderComponent } from "@/components/ui/page-header";

// CI's @vue/test-utils (2.2.7) can't type mount() of a component with named slots (TS2769).
// eslint-disable-next-line @typescript-eslint/no-explicit-any
const PageHeader = PageHeaderComponent as any;

describe("PageHeader", () => {
  it("is the page's h1 at the one title size, with its line under it, clear of the phone's menu button", () => {
    const wrapper = mount(PageHeader, { props: { title: "Repositories", description: "Git repositories Fleet found." } });
    const title = wrapper.get("h1");
    expect(title.text()).toBe("Repositories");
    expect(title.classes()).toEqual(expect.arrayContaining(["text-2xl", "font-semibold", "tracking-tight"]));
    expect(wrapper.get("p").text()).toBe("Git repositories Fleet found.");
    expect(wrapper.classes()).toContain("phone-menu-lane");
    expect(wrapper.find("[data-slot='page-header-actions']").exists()).toBe(false);
    expect(wrapper.find("nav").exists()).toBe(false);
  });

  it("puts actions on the right, things beside the title in its row, and heads a pane with h2 when asked", () => {
    const wrapper = mount(PageHeader, {
      props: { title: "Jobs", level: 2, bar: true },
      slots: { meta: () => h("span", { class: "chip" }, "Queue"), actions: () => h("button", "New job") },
    });
    expect(wrapper.find("h1").exists()).toBe(false);
    expect(wrapper.get("h2").text()).toBe("Jobs");
    expect(wrapper.classes()).toContain("page-header--bar");
    expect(wrapper.get(".page-header__title-row .chip").text()).toBe("Queue");
    expect(wrapper.get("[data-slot='page-header-actions']").text()).toBe("New job");
  });

  it("reads one link as a back link and several as a trail, following each in the app", async () => {
    const back = vi.fn();
    const one = mount(PageHeader, { props: { title: "Daily digest", trail: [{ label: "Automations", href: "/automations", onSelect: back }] } });
    expect(one.get("nav").attributes("aria-label")).toBe("Back");
    await one.get("nav a").trigger("click");
    expect(back).toHaveBeenCalledTimes(1);

    const trail = mount(PageHeader, {
      props: {
        title: "Fix the flaky test",
        trail: [{ label: "GitHub", href: "/github", onSelect: vi.fn() }, { label: "acme / rocket", href: "/github/acme/rocket" }, { label: "Issues" }],
      },
    });
    expect(trail.get("nav").attributes("aria-label")).toBe("Breadcrumbs");
    expect(trail.findAll("nav a").map((a) => a.attributes("href"))).toEqual(["/github", "/github/acme/rocket"]);
    expect(trail.get("nav").text()).toContain("Issues");
  });

  it("lets a page draw its own heading in the title's place, such as a rename form", () => {
    const wrapper = mount(PageHeader, {
      props: { title: "Delivery board", eyebrow: "Persistent board" },
      slots: { heading: () => h("form", { class: "rename" }) },
    });
    expect(wrapper.find("h1").exists()).toBe(false);
    expect(wrapper.find("form.rename").exists()).toBe(true);
    expect(wrapper.text()).toContain("Persistent board");
  });
});
