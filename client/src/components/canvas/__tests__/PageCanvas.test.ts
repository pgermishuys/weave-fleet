import { mount } from "@vue/test-utils";
import { afterEach, describe, expect, it, vi } from "vitest";
import PageCanvas from "@/components/canvas/PageCanvas.vue";
import { useDraftState } from "@/composables/use-draft-state";
import { forgetPageStates } from "@/lib/page-bridge";
import type { ShownPage } from "@/lib/server-canvas";

vi.mock("@/lib/api-client", () => ({
  apiUrl: (path: string) => `http://fleet.test${path}`,
  apiUrlOn: (_machine: unknown, path: string) => `http://fleet.test${path}`,
}));

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
    const wrapper = mount(PageCanvas, { props: { sessionId: "s1", page } });

    const frame = wrapper.get("iframe");
    expect(frame.attributes("src")).toBe("http://fleet.test/pages/pg_0123456789abcdef0123456789abcdef/options%20a.html");
    expect(frame.attributes("title")).toBe("Settings options");
    expect(frame.attributes("sandbox")).toContain("allow-scripts");
    expect(frame.attributes("sandbox")).not.toContain("allow-same-origin");
    expect(wrapper.text()).toContain("options a.html");
    expect(wrapper.find(".page-canvas__path").attributes("title") ?? wrapper.find(".page-canvas__source").attributes("title"))
      .toBe("/tmp/mockups/settings/options a.html");
  });

  it("says what a page Fleet wrote itself is, in place of a file", () => {
    const wrapper = mount(PageCanvas, {
      props: { sessionId: "s1", page: { ...page, source: "walkthrough:Orders", label: "Walkthrough · compared with main at 55650b2" } },
    });

    expect(wrapper.get(".page-canvas__source").text()).toBe("Walkthrough · compared with main at 55650b2");
    expect(wrapper.text()).not.toContain("walkthrough:Orders");
  });

  it("loads the page again when the agent shows it again, and on Reload", async () => {
    const wrapper = mount(PageCanvas, { props: { sessionId: "s1", page } });
    const first = wrapper.get("iframe").element;

    await wrapper.setProps({ page: { ...page, shownAt: "2026-09-28T10:05:00Z" } });
    const second = wrapper.get("iframe").element;
    expect(second).not.toBe(first);

    await wrapper.get('button[aria-label="Reload"]').trigger("click");
    expect(wrapper.get("iframe").element).not.toBe(second);
  });

  it("lists the links that won't load", () => {
    const wrapper = mount(PageCanvas, { props: { sessionId: "s1", page: { ...page, warnings: ["/styles.css won't load: use a path relative to the page, inside its folder."] } } });

    expect(wrapper.get('[aria-label="Links that won\'t load"]').text()).toContain("/styles.css won't load");
  });

  it("opens the page in a tab of its own", async () => {
    const open = vi.spyOn(window, "open").mockReturnValue(null);
    const wrapper = mount(PageCanvas, { props: { sessionId: "s1", page } });

    await wrapper.get('button[aria-label="Open in a new tab"]').trigger("click");

    expect(open).toHaveBeenCalledWith("http://fleet.test/pages/pg_0123456789abcdef0123456789abcdef/options%20a.html", "_blank", "noopener");
    open.mockRestore();
  });

  describe("messages from the page", () => {
    afterEach(() => {
      forgetPageStates();
      document.body.innerHTML = "";
    });

    function mountAttached(sessionId = "s-page") {
      return mount(PageCanvas, { props: { sessionId, page }, attachTo: document.body });
    }

    function frameWindow(wrapper: ReturnType<typeof mountAttached>): Window {
      return (wrapper.get("iframe").element as HTMLIFrameElement).contentWindow!;
    }

    // A sandboxed page without allow-same-origin always posts from the opaque origin "null".
    function post(source: Window | null, data: unknown, origin = "null"): void {
      window.dispatchEvent(new MessageEvent("message", { data, origin, source }));
    }

    it("puts a reply in the composer, and sends nothing", () => {
      const focus = vi.fn();
      window.addEventListener("weave:command-focus-prompt", focus);
      const wrapper = mountAttached("s-reply");

      post(frameWindow(wrapper), { type: "fleet:page-reply", text: "# Re: Plan\n## Decisions" });

      expect(useDraftState("s-reply", { agentId: "a", modelId: "m" }).draft.text).toBe("# Re: Plan\n## Decisions");
      expect(focus).toHaveBeenCalledTimes(1);
      expect((focus.mock.calls[0][0] as CustomEvent).detail).toEqual({ sessionId: "s-reply" });
      window.removeEventListener("weave:command-focus-prompt", focus);
      wrapper.unmount();
    });

    it("keeps what the user already typed above the reply", () => {
      const wrapper = mountAttached("s-typed");
      useDraftState("s-typed", { agentId: "a", modelId: "m" }).setText("one more thing  ");

      post(frameWindow(wrapper), { type: "fleet:page-reply", text: "# Re: Plan" });

      expect(useDraftState("s-typed", { agentId: "a", modelId: "m" }).draft.text).toBe("one more thing\n\n# Re: Plan");
      wrapper.unmount();
    });

    it("ignores messages from any other window or origin", () => {
      const wrapper = mountAttached("s-other");
      const reply = { type: "fleet:page-reply", text: "not from the page" };

      post(window, reply);
      post(null, reply);
      post(frameWindow(wrapper), reply, "http://fleet.test");

      expect(useDraftState("s-other", { agentId: "a", modelId: "m" }).draft.text).toBe("");
      wrapper.unmount();
    });

    it("answers a hello with the state the page saved, also after the page is shown again", async () => {
      const wrapper = mountAttached();
      const firstFrame = wrapper.get("iframe").element;
      const first = frameWindow(wrapper);
      const firstAnswer = vi.spyOn(first, "postMessage");

      post(first, { type: "fleet:page-hello" });
      expect(firstAnswer).toHaveBeenCalledWith({ type: "fleet:page-state", state: null }, "*");

      post(first, { type: "fleet:page-state", state: { answers: { keep: "db" } } });

      // Showing the page again loads a new frame for the same page id.
      await wrapper.setProps({ page: { ...page, shownAt: "2026-09-28T10:05:00Z" } });
      expect(wrapper.get("iframe").element).not.toBe(firstFrame);
      const second = frameWindow(wrapper);
      const secondAnswer = vi.spyOn(second, "postMessage");

      post(second, { type: "fleet:page-hello" });
      expect(secondAnswer).toHaveBeenCalledWith({ type: "fleet:page-state", state: { answers: { keep: "db" } } }, "*");
      wrapper.unmount();
    });

    it("stops listening once the canvas is gone", () => {
      const wrapper = mountAttached("s-gone");
      const source = frameWindow(wrapper);
      wrapper.unmount();

      post(source, { type: "fleet:page-reply", text: "too late" });

      expect(useDraftState("s-gone", { agentId: "a", modelId: "m" }).draft.text).toBe("");
    });
  });
});
