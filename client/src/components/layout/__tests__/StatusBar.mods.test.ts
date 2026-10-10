import { mount } from "@vue/test-utils";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ref } from "vue";
import type { SessionListItem } from "@/api/client";
import StatusBar from "@/components/layout/StatusBar.vue";
import { statusChips } from "@/lib/mods/points";
import { useSessionsStore } from "@/stores/sessions";

// The route the bar is on: a session view, or another page with the session still the active one.
const route = vi.hoisted(() => ({ pathname: "/sessions/s1" }));
vi.mock("@tanstack/vue-router", () => ({
  useRouter: () => ({}),
  useLocation: () => ref(route.pathname),
}));
vi.mock("@/api/client", () => ({ api: { GET: vi.fn(async () => ({ data: [], response: { ok: true, status: 200 } })) } }));

const mounted: Array<{ unmount: () => void }> = [];
function mountBar() {
  const wrapper = mount(StatusBar);
  mounted.push(wrapper);
  return wrapper;
}

function openSession(id = "s1"): void {
  const sessions = useSessionsStore();
  sessions.setSessions([
    { session: { id, title: "Session" }, instanceId: "i1", activityStatus: "idle", sessionStatus: "idle", totalTokens: 1200 } as unknown as SessionListItem,
  ]);
  sessions.setActiveSessionId(id);
}

const tree = { type: "Text", props: {}, children: ["build ok"] } as never;
const author = (draft = false) => [{ name: "ci-mod", draft }] as never;

describe("StatusBar mod chip", () => {
  beforeEach(() => {
    route.pathname = "/sessions/s1";
    globalThis.localStorage?.clear();
    statusChips.clear();
  });
  afterEach(() => {
    for (const wrapper of mounted.splice(0)) wrapper.unmount();
    statusChips.clear();
  });

  it("draws nothing for a session that is not on screen, nor with no session", () => {
    statusChips.contribute("t", [{ sessionId: "other", tree, mods: author() }]);
    expect(mountBar().find("[data-testid=mod-status-chip]").exists()).toBe(false);
    openSession("s1");
    expect(mountBar().find("[data-testid=mod-status-chip]").exists()).toBe(false);
  });

  it.each([["Settings", "/settings"], ["Workflows", "/workflows"], ["Automations", "/automations"], ["the dashboard", "/"], ["a new session", "/sessions/new"]])(
    "draws no chip on %s, though a session is still the active one",
    (_name, pathname) => {
      openSession("s1");
      statusChips.contribute("t", [{ sessionId: "s1", tree, mods: author() }]);
      route.pathname = pathname;
      const wrapper = mountBar();
      expect(wrapper.find("[data-testid=mod-status-chip]").exists()).toBe(false);
      // The model and tokens still show: only the chip is gated on the route.
      expect(wrapper.find("[data-testid=status-bar-session]").exists()).toBe(true);
    },
  );

  it("draws the chip on a session route", () => {
    openSession("s1");
    statusChips.contribute("t", [{ sessionId: "s1", tree, mods: author() }]);
    route.pathname = "/sessions/s1";
    expect(mountBar().find("[data-testid=mod-status-chip]").exists()).toBe(true);
  });

  it("draws the chip just before the session's model and tokens", async () => {
    openSession();
    const wrapper = mountBar();
    statusChips.contribute("t", [{ sessionId: "s1", tree, mods: author() }]);
    await wrapper.vm.$nextTick();
    const end = wrapper.get(".status-bar__end").element;
    const chip = wrapper.get("[data-testid=mod-status-chip]").element;
    expect(chip.textContent).toContain("build ok");
    expect(chip.nextElementSibling).toBe(wrapper.get(".status-bar__right").element);
    expect(chip.parentElement).toBe(end);
    expect(wrapper.find("[data-testid=mod-draft-mark]").exists()).toBe(false);
  });

  it("marks a draft", () => {
    openSession();
    statusChips.contribute("t", [{ sessionId: "s1", tree, mods: author(true) }]);
    expect(mountBar().find("[data-testid=mod-status-chip] [data-testid=mod-draft-mark]").exists()).toBe(true);
  });

  it("draws nothing for a null or invalid tree, and removal restores the bar", async () => {
    openSession();
    const before = mountBar().html();
    statusChips.contribute("t", [{ sessionId: "s1", tree: null, mods: author() }]);
    expect(mountBar().find("[data-testid=mod-status-chip]").exists()).toBe(false);
    statusChips.contribute("t", [{ sessionId: "s1", tree: { type: "Nope" } as never, mods: author() }]);
    expect(mountBar().find("[data-testid=mod-status-chip]").exists()).toBe(false);
    statusChips.contribute("t", [{ sessionId: "s1", tree, mods: author() }]);
    const wrapper = mountBar();
    expect(wrapper.find("[data-testid=mod-status-chip]").exists()).toBe(true);
    statusChips.removeByOwner("t");
    await wrapper.vm.$nextTick();
    expect(wrapper.html()).toBe(before);
  });

  it("sends an action from the chip as desktop", async () => {
    openSession();
    const onAction = vi.fn();
    statusChips.contribute("t", [{
      sessionId: "s1",
      tree: { type: "Button", props: { key: "go", label: "Go" }, handles: { onPress: "h1" } } as never,
      mods: author(),
      onAction,
    }]);
    await mountBar().get("[data-testid=mod-status-chip] button").trigger("click");
    expect(onAction).toHaveBeenCalledTimes(1);
    expect(onAction.mock.calls[0]).toEqual([{ handle: "h1", kind: "press" }, "desktop"]);
  });
});
