import { mount } from "@vue/test-utils";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { defineComponent, h, nextTick } from "vue";
import * as failures from "@/lib/mods/failures";
import PhoneToolRun from "@/components/phone/session/PhoneToolRun.vue";
import StepsSheet from "@/components/phone/session/StepsSheet.vue";
import { foldMessages, type FoldedStep, type PhoneBlock } from "@/lib/phone/fold-steps";
import type { AccumulatedMessage, AccumulatedToolPart } from "@/lib/client-types";
import { toolRowViews, type ToolRowView } from "@/lib/mods/points";
import type { ModWireElement } from "@/lib/mods/types";

/** Mods at a phone tool row and in the steps sheet. (No contribution = today's DOM: PhoneToolRun.characterise.test.ts.) */
type Steps = Extract<PhoneBlock, { kind: "steps" }>;
type Subagent = Extract<PhoneBlock, { kind: "subagent" }>;
type Part = Steps | Subagent;

function call(id: string, tool: string, status: string, input: Record<string, unknown> = {}, extra: Record<string, unknown> = {}): AccumulatedToolPart {
  return { type: "tool", partId: `p-${id}`, callId: `c-${id}`, tool, state: { status, input, ...extra } } as AccumulatedToolPart;
}

function fold(parts: AccumulatedToolPart[]): Part[] {
  const message = { messageId: "m1", role: "assistant", parts, createdAt: 1 } as unknown as AccumulatedMessage;
  return foldMessages([message]).filter((b): b is Part => b.kind === "steps" || b.kind === "subagent");
}

function run(parts: AccumulatedToolPart[], props: { waitingCalls?: ReadonlySet<string>; all?: boolean } = {}) {
  return mount(PhoneToolRun, { props: { parts: fold(parts), sessionId: "s1", ...props } });
}

const BottomSheet = defineComponent({ setup: (_, { slots }) => () => h("div", [slots.head?.(), slots.default?.()]) });
function sheet(parts: AccumulatedToolPart[]) {
  const steps = (fold(parts)[0] as Steps).steps as FoldedStep[];
  return mount(StepsSheet, { props: { open: true, steps, focus: steps[0], sessionId: "s1" }, global: { stubs: { BottomSheet } } });
}

/** The DOM without Vue's v-if placeholders, which come and go with the template's branches. */
const html = (w: { element: Element }) => w.element.outerHTML.replace(/<!--[\s\S]*?-->/g, "");

const text = (value: string): ModWireElement => ({ type: "Text", props: {}, children: [value] }) as unknown as ModWireElement;
const button = (label: string): ModWireElement => ({ type: "Button", props: { key: "go", label }, handles: { onPress: "h1" } }) as unknown as ModWireElement;
const author: ToolRowView["mods"] = [{ name: "M", draft: false }];
function contribute(site: "ToolUse" | "ToolResult", tree: unknown, extra: Partial<ToolRowView> = {}, callId = "c-1", sessionId = "s1") {
  return toolRowViews.contribute("test", [{ site, sessionId, tool: "bash", callId, tree: tree as ModWireElement | null, mods: author, ...extra }]);
}


const edit = call("1", "edit", "completed", { filePath: "src/a.ts", oldString: "one", newString: "two\nthree" });
const BASH = [call("1", "bash", "completed", { command: "ls -la" }, { output: "a\nb" })];
const step = (w: ReturnType<typeof run>) => w.get("[data-testid='phone-step']");

describe("PhoneToolRun with a ToolUse tree", () => {
  beforeEach(() => toolRowViews.clear());

  it("draws the tree on a second line right after the row's button and drops the check", () => {
    contribute("ToolUse", text("3 files"));
    const w = run(BASH);
    const row = step(w);
    expect(row.element.tagName).toBe("BUTTON");
    expect(row.find(".ph-tool__mod").exists()).toBe(false);
    expect(row.get(".ph-tool__d").text()).toBe("ls -la");
    expect(row.find("[aria-label='Done']").exists()).toBe(false);
    const line = row.element.nextElementSibling as HTMLElement;
    expect(line.classList.contains("ph-tool__mod")).toBe(true);
    expect(line.textContent).toBe("3 files");
  });

  it("never nests a button in a button", () => {
    contribute("ToolUse", button("Go"));
    const w = run(BASH);
    expect(w.findAll(".ph-tool button")).toHaveLength(0);
    expect(w.findAll("button button")).toHaveLength(0);
    expect(w.findAll(".ph-tool__mod button")).toHaveLength(1);
  });

  it("keeps Fleet's +N -N on the row, the tree on the line under it, and drops only the check", () => {
    contribute("ToolUse", text("t"));
    const edited = step(run([edit]));
    expect(edited.get(".ph-add").text()).toBe("+2");
    expect(edited.get(".ph-del").text()).toBe("−1");
    expect((edited.element.nextElementSibling as HTMLElement).textContent).toBe("t");
  });

  it("keeps failed, running and Needs you", () => {
    contribute("ToolUse", text("t"));
    expect(step(run([call("1", "bash", "error", { command: "false" })])).get(".ph-tool__bad").text()).toBe("failed");
    expect(step(run([call("1", "bash", "running", { command: "x" })])).find("[aria-label='Running']").exists()).toBe(true);
    const needs = step(run([call("1", "bash", "running", { command: "x" })], { waitingCalls: new Set(["c-1"]) }));
    expect(needs.get(".ph-tool__needs").text()).toBe("Needs you");
    expect(needs.element.nextElementSibling?.classList.contains("ph-tool__mod")).toBe(true);
  });

  it("a null tree draws no second line but still drops the result", () => {
    contribute("ToolUse", null);
    const row = step(run(BASH));
    expect(row.find(".ph-tool__mod").exists()).toBe(false);
    expect(row.find("[aria-label='Done']").exists()).toBe(false);
  });

  it("is keyed by session and call id, not by the tool, and draws only step rows", () => {
    contribute("ToolUse", text("yes"), { tool: "something else" }, "c-1");
    expect(run(BASH).findAll(".ph-tool__mod")).toHaveLength(1);
    expect(run([call("1", "read", "completed", { filePath: "a" })]).findAll(".ph-tool__mod")).toHaveLength(1);
    expect(run([call("2", "bash", "completed", { command: "x" })]).find(".ph-tool__mod").exists()).toBe(false);
    expect(mount(PhoneToolRun, { props: { parts: fold(BASH), sessionId: "s2" } }).find(".ph-tool__mod").exists()).toBe(false);
    expect(mount(PhoneToolRun, { props: { parts: fold(BASH) } }).find(".ph-tool__mod").exists()).toBe(false);
    contribute("ToolUse", text("sub"), {}, "c-1");
    const sub = run([call("1", "task", "completed", { description: "look", subagent_type: "explore" }, { metadata: { sessionId: "child-1" } })]);
    expect(sub.find(".ph-tool__mod").exists()).toBe(false);
  });

  it("draws Fleet's own for an invalid tree", () => {
    vi.spyOn(failures, "reportModTreeInvalid").mockImplementation(() => undefined);
    const before = html(run(BASH));
    contribute("ToolUse", { type: "Nope" });
    expect(html(run(BASH))).toBe(before);
  });

  it("redraws on contribute and restores on removal", async () => {
    const w = run(BASH);
    const before = html(w);
    const off = contribute("ToolUse", text("live"));
    await nextTick();
    expect(w.get(".ph-tool__mod").text()).toBe("live");
    off();
    await nextTick();
    expect(html(w)).toBe(before);
  });

  it("sends a press to onAction as phone, without opening the row; tapping the row opens it", async () => {
    const onAction = vi.fn();
    contribute("ToolUse", button("Go"), { onAction });
    const w = run(BASH);
    await w.get(".ph-tool__mod button").trigger("click");
    expect(onAction).toHaveBeenCalledWith({ handle: "h1", kind: "press" }, "phone");
    expect(w.emitted("open")).toBeUndefined();
    await step(w).trigger("click");
    expect(w.emitted("open")).toHaveLength(1);
  });

  it("shows no draft mark", () => {
    contribute("ToolUse", text("d"), { mods: [{ name: "M", draft: true }] });
    expect(run(BASH).find(".mod-draft").exists()).toBe(false);
  });
});

describe("StepsSheet with a ToolResult tree", () => {
  beforeEach(() => toolRowViews.clear());
  const CMD = [call("1", "bash", "completed", { command: "ls" }, { output: "a\nb" })];

  it("draws the tree in place of Fleet's detail", () => {
    contribute("ToolResult", text("custom"));
    const w = sheet(CMD);
    expect(w.get(".ph-sheet__pad").text()).toBe("custom");
    expect(w.find("[data-testid='phone-step-detail']").exists()).toBe(false);
  });

  it("draws Fleet's own detail where the tree says Fleet", () => {
    contribute("ToolResult", { type: "Box", props: {}, children: [text("top"), { type: "Fleet" }] });
    const w = sheet(CMD);
    expect(w.get(".mod-tree").text()).toContain("top");
    expect(w.get(".mod-tree [data-testid='phone-step-detail']").text()).toContain("ls");
  });

  it("a null tree is an empty detail", () => {
    contribute("ToolResult", null);
    const w = sheet(CMD);
    expect(w.find("[data-testid='phone-step-detail']").exists()).toBe(false);
  });

  it("does not draw for another call, and an invalid tree and removal restore Fleet's detail", async () => {
    vi.spyOn(failures, "reportModTreeInvalid").mockImplementation(() => undefined);
    const before = html(sheet(CMD));
    contribute("ToolResult", text("x"), {}, "c-9");
    expect(html(sheet(CMD))).toBe(before);
    toolRowViews.clear();
    contribute("ToolResult", { type: "Nope" });
    expect(html(sheet(CMD))).toBe(before);
    toolRowViews.clear();
    const w = sheet(CMD);
    const off = contribute("ToolResult", text("x"));
    await nextTick();
    expect(w.get(".ph-sheet__pad").text()).toBe("x");
    off();
    await nextTick();
    expect(html(w)).toBe(before);
  });

  it("sends a press to onAction as phone", async () => {
    const onAction = vi.fn();
    contribute("ToolResult", button("Go"), { onAction });
    await sheet(CMD).get(".ph-sheet__pad button").trigger("click");
    expect(onAction).toHaveBeenCalledWith({ handle: "h1", kind: "press" }, "phone");
  });

  it("the list of steps (nothing opened) is not replaced", () => {
    contribute("ToolResult", text("custom"));
    const steps = (fold(CMD)[0] as Steps).steps as FoldedStep[];
    const w = mount(StepsSheet, { props: { open: true, steps, sessionId: "s1" }, global: { stubs: { BottomSheet } } });
    expect(w.find("[data-testid='phone-step']").exists()).toBe(true);
    expect(w.text()).not.toContain("custom");
  });
});

describe("PhoneToolRun rows at scale", () => {
  beforeEach(() => toolRowViews.clear());

  /** 200 runs of one call each, counting how many redraw. */
  function runs(n = 200) {
    const state = { updates: 0 };
    const List = defineComponent({
      setup: () => () =>
        h("div", Array.from({ length: n }, (_, i) =>
          h(PhoneToolRun, {
            key: i,
            parts: fold([call(String(i), "bash", "completed", { command: `cmd ${i}` })]),
            sessionId: "s1",
            onVnodeUpdated: () => { state.updates++; },
          }))),
    });
    return { state, wrapper: mount(List) };
  }

  it("contributing one call's view redraws that call's run only", async () => {
    const { state, wrapper } = runs();
    await nextTick();
    state.updates = 0;
    contribute("ToolUse", text("ok"), {}, "c-7");
    await nextTick();
    expect(state.updates).toBe(1);
    expect(wrapper.findAll(".ph-tool__mod")).toHaveLength(1);
    wrapper.unmount();
  });

  it("contributing a view for a call that is not on screen redraws nothing", async () => {
    const { state, wrapper } = runs();
    await nextTick();
    state.updates = 0;
    contribute("ToolUse", text("ok"), {}, "c-nope");
    contribute("ToolUse", text("ok"), {}, "c-7", "other-session");
    await nextTick();
    expect(state.updates).toBe(0);
    wrapper.unmount();
  });
});
