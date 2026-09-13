import { flushPromises, mount } from "@vue/test-utils";
import { defineComponent, h } from "vue";
import { beforeEach, describe, expect, it, vi } from "vitest";
import TerminalDrawer from "@/components/terminal/TerminalDrawer.vue";
import type { TerminalSummary } from "@/lib/terminal-api";
import { useTerminalsStore } from "@/stores/terminals";

const { openNewTerminalMock, closeTerminalTabMock, clearMock } = vi.hoisted(() => ({
  openNewTerminalMock: vi.fn(),
  closeTerminalTabMock: vi.fn(),
  clearMock: vi.fn(),
}));

vi.mock("@/composables/use-session-terminals", () => ({
  openNewTerminal: openNewTerminalMock,
  closeTerminalTab: closeTerminalTabMock,
}));

// xterm needs a real layout engine; the drawer only needs something in its place.
vi.mock("@/components/terminal/TerminalView.vue", () => ({
  default: defineComponent({
    name: "TerminalView",
    props: { sessionId: { type: String, required: true }, terminalId: { type: String, required: true }, shown: Boolean },
    emits: ["size", "focus", "ended"],
    setup(props, { expose }) {
      expose({ clear: () => clearMock(props.terminalId), focus: () => {} });
      return () => h("div", { class: "stub-view", "data-id": props.terminalId, "data-shown": String(props.shown) });
    },
  }),
}));

function terminal(id: string, title = "zsh"): TerminalSummary {
  return { id, title, status: "running", createdAt: "2026-09-13T10:00:00Z" };
}

function mountDrawer() {
  return mount(TerminalDrawer, { props: { sessionId: "s1", directory: "/home/me/source/weave-fleet" } });
}

describe("TerminalDrawer", () => {
  beforeEach(() => {
    globalThis.localStorage?.clear();
    openNewTerminalMock.mockReset();
    closeTerminalTabMock.mockReset();
    clearMock.mockReset();
  });

  it("renders nothing, and starts nothing, until the drawer is opened", async () => {
    const store = useTerminalsStore();
    store.setTerminals("s1", [terminal("t1")]);

    const wrapper = mountDrawer();
    await flushPromises();

    expect(wrapper.find(".terminal-drawer").exists()).toBe(false);
    expect(wrapper.findAll(".stub-view")).toHaveLength(0);
    expect(openNewTerminalMock).not.toHaveBeenCalled();
  });

  it("starts a shell when opened with no terminals, once the list has loaded", async () => {
    const store = useTerminalsStore();
    openNewTerminalMock.mockImplementation(async (sessionId: string) => {
      store.add(sessionId, terminal("t1"), true);
      return null;
    });
    store.setOpen("s1", true);

    const wrapper = mountDrawer();
    await flushPromises();
    expect(openNewTerminalMock).not.toHaveBeenCalled();

    store.setTerminals("s1", []);
    await flushPromises();

    expect(openNewTerminalMock).toHaveBeenCalledWith("s1", 120, 14);
    expect(wrapper.findAll(".stub-view").map((view) => view.attributes("data-id"))).toEqual(["t1"]);
  });

  it("shows why a shell couldn't start, and tries again on request", async () => {
    const store = useTerminalsStore();
    openNewTerminalMock.mockResolvedValueOnce("The session's folder /gone doesn't exist any more.");
    store.setTerminals("s1", []);
    store.setOpen("s1", true);

    const wrapper = mountDrawer();
    await flushPromises();

    expect(wrapper.get("[role=alert]").text()).toContain("/gone");
    openNewTerminalMock.mockResolvedValueOnce(null);
    await wrapper.get(".terminal-drawer__retry").trigger("click");
    expect(openNewTerminalMock).toHaveBeenCalledTimes(2);
  });

  it("mounts a tab's terminal the first time it shows, and keeps it", async () => {
    const store = useTerminalsStore();
    store.setTerminals("s1", [terminal("t1"), terminal("t2", "zsh 2")]);
    store.setOpen("s1", true);

    const wrapper = mountDrawer();
    await flushPromises();
    expect(wrapper.findAll(".stub-view").map((view) => view.attributes("data-id"))).toEqual(["t1"]);

    await wrapper.findAll(".terminal-tab")[1].trigger("click");
    const views = wrapper.findAll(".stub-view");
    expect(views.map((view) => view.attributes("data-id"))).toEqual(["t1", "t2"]);
    expect(views.map((view) => view.attributes("data-shown"))).toEqual(["false", "true"]);
  });

  it("closes a tab through the server, and clears the one showing", async () => {
    const store = useTerminalsStore();
    store.setTerminals("s1", [terminal("t1")]);
    store.setOpen("s1", true);
    const wrapper = mountDrawer();
    await flushPromises();

    await wrapper.get('[aria-label="Clear terminal"]').trigger("click");
    await wrapper.get(".terminal-tab__close").trigger("click");

    expect(clearMock).toHaveBeenCalledWith("t1");
    expect(closeTerminalTabMock).toHaveBeenCalledWith("s1", "t1");
  });

  it("hides without ending the shells", async () => {
    const store = useTerminalsStore();
    store.setTerminals("s1", [terminal("t1")]);
    store.setOpen("s1", true);
    const wrapper = mountDrawer();
    await flushPromises();

    await wrapper.get('[aria-label="Hide terminal"]').trigger("click");

    expect(store.isOpen("s1")).toBe(false);
    expect(closeTerminalTabMock).not.toHaveBeenCalled();
    expect(wrapper.findAll(".stub-view")).toHaveLength(1);
  });

  it("moves between tabs with the arrow keys", async () => {
    const store = useTerminalsStore();
    store.setTerminals("s1", [terminal("t1"), terminal("t2", "zsh 2")]);
    store.setOpen("s1", true);
    const wrapper = mountDrawer();
    await flushPromises();

    await wrapper.get("[role=tablist]").trigger("keydown", { key: "ArrowRight" });

    expect(store.activeFor("s1")?.id).toBe("t2");
  });

  it("resizes from the keyboard, within its limits", async () => {
    const store = useTerminalsStore();
    store.setTerminals("s1", [terminal("t1")]);
    store.setOpen("s1", true);
    const wrapper = mountDrawer();
    await flushPromises();
    const before = store.height;

    await wrapper.get("[role=separator]").trigger("keydown", { key: "ArrowUp" });
    expect(store.height).toBe(before + 24);

    for (let i = 0; i < 20; i++) await wrapper.get("[role=separator]").trigger("keydown", { key: "ArrowDown" });
    expect(store.height).toBe(140);
  });

  it("shows the session folder", async () => {
    const store = useTerminalsStore();
    store.setTerminals("s1", [terminal("t1")]);
    store.setOpen("s1", true);
    const wrapper = mountDrawer();
    await flushPromises();

    expect(wrapper.get(".terminal-drawer__cwd").attributes("title")).toBe("/home/me/source/weave-fleet");
  });
});
