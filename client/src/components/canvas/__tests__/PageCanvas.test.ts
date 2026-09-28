import { mount } from "@vue/test-utils";
import { describe, expect, it, vi } from "vitest";
import PageCanvas from "@/components/canvas/PageCanvas.vue";
import type { ShownPage } from "@/lib/server-canvas";

vi.mock("@/lib/api-client", () => ({ apiUrl: (path: string) => `http://fleet.test${path}` }));

const page: ShownPage & { title: string } = {
  pageId: "pg_0123456789abcdef0123456789abcdef",
  entry: "options a.html",
  source: "/tmp/mockups/settings/options a.html",
  files: 3,
  bytes: 4096,
  shownAt: "2026-09-28T10:00:00Z",
  warnings: [],
  title: "Settings options",
};

describe("PageCanvas", () => {
  it("frames Fleet's copy of the page in a sandbox without Fleet's origin", () => {
    const wrapper = mount(PageCanvas, { props: { page } });

    const frame = wrapper.get("iframe");
    expect(frame.attributes("src")).toBe("http://fleet.test/pages/pg_0123456789abcdef0123456789abcdef/options%20a.html");
    expect(frame.attributes("title")).toBe("Settings options");
    expect(frame.attributes("sandbox")).toContain("allow-scripts");
    expect(frame.attributes("sandbox")).not.toContain("allow-same-origin");
    expect(wrapper.text()).toContain("options a.html");
    expect(wrapper.find(".page-canvas__path").attributes("title") ?? wrapper.find(".page-canvas__source").attributes("title"))
      .toBe("/tmp/mockups/settings/options a.html");
  });

  it("loads the page again when the agent shows it again, and on Reload", async () => {
    const wrapper = mount(PageCanvas, { props: { page } });
    const first = wrapper.get("iframe").element;

    await wrapper.setProps({ page: { ...page, shownAt: "2026-09-28T10:05:00Z" } });
    const second = wrapper.get("iframe").element;
    expect(second).not.toBe(first);

    await wrapper.get('button[aria-label="Reload"]').trigger("click");
    expect(wrapper.get("iframe").element).not.toBe(second);
  });

  it("lists the links that won't load", () => {
    const wrapper = mount(PageCanvas, { props: { page: { ...page, warnings: ["/styles.css won't load: use a path relative to the page, inside its folder."] } } });

    expect(wrapper.get('[aria-label="Links that won\'t load"]').text()).toContain("/styles.css won't load");
  });

  it("opens the page in a tab of its own", async () => {
    const open = vi.spyOn(window, "open").mockReturnValue(null);
    const wrapper = mount(PageCanvas, { props: { page } });

    await wrapper.get('button[aria-label="Open in a new tab"]').trigger("click");

    expect(open).toHaveBeenCalledWith("http://fleet.test/pages/pg_0123456789abcdef0123456789abcdef/options%20a.html", "_blank", "noopener");
    open.mockRestore();
  });
});
