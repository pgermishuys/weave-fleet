import { mount } from "@vue/test-utils";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { defineComponent, h, nextTick } from "vue";
import * as failures from "@/lib/mods/failures";
import ToolCard from "@/components/session/ToolCard.vue";
import MessageToolList from "@/components/session/MessageToolList.vue";
import { toolRowViews, type ToolRowView } from "@/lib/mods/points";
import type { ModWireElement } from "@/lib/mods/types";

/** Mods at a tool row: a contribution draws on the line or in the body. (No contribution = today's DOM: ToolCard.characterise.test.ts.) */
const diffLines = [
  { type: "remove" as const, content: "-one", oldLineNumber: 1 },
  { type: "add" as const, content: "+two", newLineNumber: 1 },
  { type: "add" as const, content: "+three", newLineNumber: 2 },
];
const CASES: Record<string, Record<string, unknown>> = {
  "bash with output and preview": { kind: "Bash", title: "ls -la", output: "a\nb", preview: "└ a (2 lines)", summary: "Listed" },
  "edit with diff stats": { kind: "Edit", title: "src/a.ts", diffLines },
  empty: { kind: "Bash", title: "true" },
};

function card(props: Record<string, unknown>) {
  const pinia = createPinia();
  setActivePinia(pinia);
  return mount(ToolCard, { global: { plugins: [pinia] }, props: { id: "c1", title: "x", sessionId: "s1", ...props }, attachTo: document.body });
}

/** The DOM without Vue's v-if placeholders, which come and go with the template's branches. */
const html = (w: { element: Element }) => w.element.outerHTML.replace(/<!--[\s\S]*?-->/g, "");

const text = (value: string): ModWireElement => ({ type: "Text", props: {}, children: [value] }) as unknown as ModWireElement;
const button = (label: string): ModWireElement => ({ type: "Button", props: { key: "go", label }, handles: { onPress: "h1" } }) as unknown as ModWireElement;
const FLEET = { type: "Fleet" } as ModWireElement;
const row = (...children: unknown[]): ModWireElement => ({ type: "Box", props: { flexDirection: "row" }, children }) as unknown as ModWireElement;
const author: ToolRowView["mods"] = [{ name: "M", draft: false }];

function contribute(site: "ToolUse" | "ToolResult", tree: unknown, extra: Partial<ToolRowView> = {}, callId = "call-1", sessionId = "s1") {
  return toolRowViews.contribute("test", [{ site, sessionId, tool: "bash", callId, tree: tree as ModWireElement | null, mods: author, ...extra }]);
}

const BASH = { ...CASES["bash with output and preview"], callId: "call-1" };
const EDIT = { ...CASES["edit with diff stats"], callId: "call-1" };
const mount_ = (props: Record<string, unknown>) => card(props);

describe("ToolCard with a ToolUse tree", () => {
  beforeEach(() => toolRowViews.clear());

  it("draws the tree in the header and drops the check and the preview", () => {
    const base = mount_({ ...BASH });
    expect(base.find("[aria-label='Completed']").exists()).toBe(true);
    expect(base.find(".tool-preview").exists()).toBe(true);
    contribute("ToolUse", text("3 files"));
    const w = mount_(BASH);
    expect(w.get(".tool-header .tool-header__mod").text()).toBe("3 files");
    expect(w.find("[aria-label='Completed']").exists()).toBe(false);
    expect(w.find(".tool-preview").exists()).toBe(false);
    // The body is still Fleet's own.
    expect(w.find("[data-testid='tool-card-output']").exists()).toBe(true);
  });

  it("keeps Fleet's +N -N after the tree, and drops only the check and the preview", () => {
    contribute("ToolUse", text("hi"));
    const w = mount_(EDIT);
    expect(w.get(".tool-header__mod").text()).toBe("hi");
    expect(w.get(".tool-header__adds").text()).toBe("+2");
    expect(w.get(".tool-header__removes").text()).toBe("−1");
    const kids = [...w.get(".tool-header").element.children];
    expect(kids.findIndex((el) => el.classList.contains("tool-header__mod"))).toBeLessThan(kids.findIndex((el) => el.classList.contains("tool-header__result")));
    // No diff stats: nothing after the tree, and no check.
    expect(mount_(BASH).find(".tool-header__result").exists()).toBe(false);
    expect(mount_(BASH).find("[aria-label='Completed']").exists()).toBe(false);
  });

  it("keeps Running, Error, Stopped, Background and the buttons", () => {
    contribute("ToolUse", text("hi"));
    for (const [status, sel] of [["Running", ".tool-header__status"], ["Error", ".tool-header__status"], ["Stopped", "[data-testid='tool-card-stopped']"], ["Background", "[data-testid='tool-card-background']"]]) {
      const s = mount_({ ...BASH, status });
      expect(s.find(sel).exists(), status).toBe(true);
      expect(s.find(".tool-header__mod").exists()).toBe(true);
      // The status comes after the tree.
      const kids = [...s.get(".tool-header").element.children];
      expect(kids.findIndex((el) => el.classList.contains("tool-header__mod"))).toBeLessThan(kids.findIndex((el) => el.matches(".tool-header__status")));
    }
    const b = mount_({ ...BASH, canvasId: "cv", improvable: true });
    expect(b.find("[data-testid='tool-card-show']").exists()).toBe(true);
    expect(b.find("[data-testid='tool-card-improve']").exists()).toBe(true);
  });

  it("a null tree draws nothing but still drops the result and the preview", () => {
    contribute("ToolUse", null);
    const w = mount_(BASH);
    expect(w.find(".tool-header__mod").exists()).toBe(true);
    expect(w.find(".tool-header__mod").text()).toBe("");
    expect(w.find("[aria-label='Completed']").exists()).toBe(false);
    expect(w.find(".tool-preview").exists()).toBe(false);
  });

  it("is keyed by session and call id, not by the tool", () => {
    contribute("ToolUse", text("yes"), { tool: "something else" }, "call-1", "s1");
    expect(mount_({ ...BASH, kind: "Bash" }).find(".tool-header__mod").exists()).toBe(true);
    expect(mount_({ ...BASH, kind: "Read" }).find(".tool-header__mod").exists()).toBe(true);
    expect(mount_({ ...BASH, callId: "call-2" }).find(".tool-header__mod").exists()).toBe(false);
    expect(mount_({ ...BASH, sessionId: "s2" }).find(".tool-header__mod").exists()).toBe(false);
    expect(mount_({ ...BASH, callId: undefined }).find(".tool-header__mod").exists()).toBe(false);
    expect(mount_({ ...BASH, sessionId: undefined }).find(".tool-header__mod").exists()).toBe(false);
  });

  it("passes its session id to the tree", () => {
    contribute("ToolUse", text("x"));
    expect(mount_(BASH).findComponent({ name: "ModTree" }).props()).toMatchObject({ site: "ToolUse", sessionId: "s1" });
  });

  it("draws Fleet's own for an invalid tree", () => {
    vi.spyOn(failures, "reportModTreeInvalid").mockImplementation(() => undefined);
    contribute("ToolUse", { type: "Nope" });
    const w = mount_(BASH);
    expect(w.find(".tool-header__mod").exists()).toBe(false);
    expect(w.find("[aria-label='Completed']").exists()).toBe(true);
    expect(w.find(".tool-preview").exists()).toBe(true);
  });

  it("redraws when contributed and when removed, and removal restores Fleet's drawing", async () => {
    const w = mount_(BASH);
    const before = html(w);
    const off = contribute("ToolUse", text("live"));
    await nextTick();
    expect(w.find(".tool-header__mod").text()).toBe("live");
    off();
    await nextTick();
    expect(html(w)).toBe(before);
  });

  it("sends a press to onAction as desktop, and does not toggle the row", async () => {
    const onAction = vi.fn();
    contribute("ToolUse", row(button("Go")), { onAction });
    const w = mount_({ ...BASH, initiallyCollapsed: true });
    const details = w.get("details").element as HTMLDetailsElement;
    expect(details.open).toBe(false);
    await w.get(".tool-header__mod button").trigger("click");
    expect(onAction).toHaveBeenCalledWith({ handle: "h1", kind: "press" }, "desktop");
    expect(details.open).toBe(false);
  });

  it("shows no draft mark on the row", () => {
    contribute("ToolUse", text("d"), { mods: [{ name: "M", draft: true }] });
    expect(mount_(BASH).find(".mod-draft").exists()).toBe(false);
  });
});

describe("ToolCard with a ToolResult tree", () => {
  beforeEach(() => toolRowViews.clear());

  it("replaces the body, keeping the line, the result and the preview", () => {
    contribute("ToolResult", text("custom body"));
    const w = mount_(BASH);
    const body = w.get(".tool-body");
    expect(body.text()).toBe("custom body");
    expect(body.find(".tool-output").exists()).toBe(false);
    expect(w.find(".tool-header__mod").exists()).toBe(false);
    expect(w.find("[aria-label='Completed']").exists()).toBe(true);
    expect(w.find(".tool-preview").exists()).toBe(true);
  });

  it("draws Fleet's own body where the tree says Fleet", () => {
    contribute("ToolResult", { type: "Box", props: {}, children: [text("above"), FLEET] });
    const body = mount_(BASH).get(".tool-body");
    expect(body.text()).toContain("above");
    expect(body.get(".mod-tree .tool-output").text()).toBe("a\nb");
    expect(body.get(".mod-tree .tool-summary").text()).toBe("Listed");
  });

  it("a null tree is an empty body, not No output captured", () => {
    contribute("ToolResult", null);
    const w = mount_({ ...CASES.empty, callId: "call-1" });
    expect(w.find(".tool-empty").exists()).toBe(false);
    expect(w.get(".tool-body").text()).toBe("");
  });

  it("draws Fleet's own for an invalid tree, and after removal", async () => {
    vi.spyOn(failures, "reportModTreeInvalid").mockImplementation(() => undefined);
    const w0 = mount_(BASH);
    const before = html(w0);
    contribute("ToolResult", { type: "Nope" });
    expect(html(mount_(BASH))).toBe(before);
    toolRowViews.clear();
    contribute("ToolResult", text("x"));
    const w = mount_(BASH);
    toolRowViews.clear();
    await nextTick();
    expect(html(w)).toBe(before);
  });

  it("sends a press to onAction as desktop", async () => {
    const onAction = vi.fn();
    contribute("ToolResult", button("Go"), { onAction });
    await mount_(BASH).get(".tool-body button").trigger("click");
    expect(onAction).toHaveBeenCalledWith({ handle: "h1", kind: "press" }, "desktop");
  });

  it("both sites can draw at once", () => {
    contribute("ToolUse", text("line"));
    contribute("ToolResult", text("body"));
    const w = mount_(BASH);
    expect(w.get(".tool-header__mod").text()).toBe("line");
    expect(w.get(".tool-body").text()).toBe("body");
  });
});

describe("MessageToolList", () => {
  beforeEach(() => toolRowViews.clear());
  it("passes the session and call ids to the row", () => {
    contribute("ToolUse", text("via list"), {}, "call-9", "s1");
    const pinia = createPinia();
    setActivePinia(pinia);
    const w = mount(MessageToolList, {
      global: { plugins: [pinia] },
      props: { sessionId: "s1", tools: [{ id: "t1", callId: "call-9", title: "ls", kind: "Bash", status: "Completed", summary: "", output: "", diffLines: [], initiallyCollapsed: true, preview: "", isPatternTool: false }] as never },
    });
    expect(w.get(".tool-header__mod").text()).toBe("via list");
  });
});

describe("ToolCard rows at scale", () => {
  beforeEach(() => toolRowViews.clear());

  /** 500 rows, counting how many redraw (a render spy on each card's update). */
  function rows(n = 500) {
    const pinia = createPinia();
    setActivePinia(pinia);
    const state = { updates: 0 };
    const List = defineComponent({
      setup: () => () =>
        h("div", Array.from({ length: n }, (_, i) =>
          h(ToolCard, {
            key: i,
            id: `t${i}`,
            callId: `call-${i}`,
            sessionId: "s1",
            kind: "Bash",
            title: `cmd ${i}`,
            output: "x",
            onVnodeUpdated: () => { state.updates++; },
          }))),
    });
    return { state, wrapper: mount(List, { global: { plugins: [pinia] } }) };
  }

  it("contributing one row's view redraws that row only", async () => {
    const { state, wrapper } = rows();
    await nextTick();
    state.updates = 0;
    contribute("ToolUse", text("ok"), {}, "call-7");
    await nextTick();
    expect(state.updates).toBe(1);
    expect(wrapper.findAll(".tool-header__mod")).toHaveLength(1);
    wrapper.unmount();
  });

  it("contributing a view for a call that is not on screen redraws nothing", async () => {
    const { state, wrapper } = rows();
    await nextTick();
    state.updates = 0;
    contribute("ToolUse", text("ok"), {}, "call-not-here");
    contribute("ToolResult", null, {}, "call-7", "other-session");
    await nextTick();
    expect(state.updates).toBe(0);
    wrapper.unmount();
  });

  it("removing one row's view redraws that row only", async () => {
    const { state, wrapper } = rows(100);
    const off = contribute("ToolUse", text("ok"), {}, "call-3");
    await nextTick();
    state.updates = 0;
    off();
    await nextTick();
    expect(state.updates).toBe(1);
    expect(wrapper.find(".tool-header__mod").exists()).toBe(false);
    wrapper.unmount();
  });
});
