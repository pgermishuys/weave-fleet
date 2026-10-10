import { mount } from "@vue/test-utils";
import { afterEach, describe, expect, it, vi } from "vitest";
import { createPinia } from "pinia";
import type { ToolCardItem } from "@/components/session/activity-stream-tool-card";

vi.mock("@tanstack/vue-router", () => ({ useRouter: () => ({ navigate: vi.fn() }) }));

const { default: MessageBubble } = await import("@/components/session/MessageBubble.vue");
const { default: AgentTaskRow } = await import("@/components/session/AgentTaskRow.vue");
const { default: ToolCard } = await import("@/components/session/ToolCard.vue");
const { default: ToolScreenshot } = await import("@/components/session/ToolScreenshot.vue");
const { default: BrowserSteps } = await import("@/components/session/BrowserSteps.vue");
const { default: ConversationPage } = await import("@/components/session/ConversationPage.vue");

/**
 * Characterizes how MessageBubble picks what to draw for each tool call (MessageBubble.vue, the .msg-tools block and
 * the pages below it). Written before the row moves into one component; the pins describe today, not an ideal.
 */
let wrapper: { unmount(): void } | undefined;
afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
});

function tool(overrides: Partial<ToolCardItem> & { id: string }): ToolCardItem {
  return { title: "A call", kind: "bash", status: "Completed", initiallyCollapsed: true, ...overrides };
}

const delegation = {
  href: "/sessions/child-1?instanceId=inst-1&parentSessionId=parent-1",
  childSessionId: "child-1",
  childInstanceId: "inst-1",
  parentSessionId: "parent-1",
  agent: "explore",
  task: "Map the entry points",
  status: "running",
};

const shot = { path: "/api/sessions/ses-1/screenshots/shot_1", width: 1280, height: 800 };
const page = { path: "/pages/pg_0123456789abcdef0123456789abcdef/times.html", id: "pg_0123456789abcdef0123456789abcdef", source: "/tmp/x/times.html" };

function bubble(tools: ToolCardItem[], sessionId: string | null = "ses-1") {
  const mounted = mount(MessageBubble, {
    // BrowserSteps, ConversationPage and ToolScreenshot are stubbed: this file pins which component is chosen and with what props.
    global: { plugins: [createPinia()], stubs: { BrowserSteps: true, ConversationPage: true, ToolScreenshot: true } },
    props: { author: "Agent", role: "assistant", body: "", tools, sessionId: sessionId ?? undefined, showIdentity: true, clusterPosition: "single" },
  });
  wrapper = mounted;
  return mounted;
}

describe("MessageBubble tool rows: which component draws a call", () => {
  it("draws an ordinary call as a ToolCard and nothing else", () => {
    const view = bubble([tool({ id: "t1", kind: "read", title: "src/a.ts" })]);

    expect(view.findAllComponents(ToolCard)).toHaveLength(1);
    expect(view.findAllComponents(AgentTaskRow)).toHaveLength(0);
    expect(view.findAllComponents(ToolScreenshot)).toHaveLength(0);
    expect(view.findAllComponents(BrowserSteps)).toHaveLength(0);
    expect(view.findAllComponents(ConversationPage)).toHaveLength(0);
  });

  it("draws an unknown call as a ToolCard too", () => {
    const view = bubble([tool({ id: "t1", kind: "mystery_tool", title: "who knows" })]);

    expect(view.findAllComponents(ToolCard)).toHaveLength(1);
    expect(view.getComponent(ToolCard).props("kind")).toBe("mystery_tool");
  });

  it("draws a call with a delegation as an AgentTaskRow, with no ToolCard", () => {
    const view = bubble([tool({ id: "t1", kind: "task", title: "Map the entry points", delegation })]);

    expect(view.findAllComponents(AgentTaskRow)).toHaveLength(1);
    expect(view.getComponent(AgentTaskRow).props("delegation")).toEqual(delegation);
    expect(view.findAllComponents(ToolCard)).toHaveLength(0);
  });

  it("draws a sub-agent call that has no delegation yet as a plain ToolCard", () => {
    // The child session isn't known yet, so there is nothing for the row to open.
    const view = bubble([tool({ id: "t1", kind: "task", title: "Map the entry points", status: "Running" })]);

    expect(view.findAllComponents(AgentTaskRow)).toHaveLength(0);
    expect(view.findAllComponents(ToolCard)).toHaveLength(1);
  });

  it("passes the call's fields to the ToolCard", () => {
    const view = bubble([
      tool({
        id: "t1",
        kind: "grep",
        title: "TODO",
        status: "Running",
        summary: "searching",
        output: "a.ts:1",
        preview: "└ a.ts:1",
        isPatternTool: true,
        canvasId: "cv_1",
        improvable: true,
        initiallyCollapsed: false,
      }),
    ]);

    expect(view.getComponent(ToolCard).props()).toMatchObject({
      id: "t1",
      kind: "grep",
      title: "TODO",
      status: "Running",
      summary: "searching",
      output: "a.ts:1",
      preview: "└ a.ts:1",
      isPatternTool: true,
      canvasId: "cv_1",
      improvable: true,
      initiallyCollapsed: false,
    });
  });

  it("draws one row per call, in order, mixing the kinds", () => {
    const view = bubble([
      tool({ id: "t1", kind: "read", title: "a.ts" }),
      tool({ id: "t2", kind: "task", title: "Sub", delegation }),
      tool({ id: "t3", kind: "edit", title: "b.ts" }),
    ]);

    const rows = view.findAll(".msg-tools > *");
    expect(rows.map((row) => row.attributes("data-testid"))).toEqual(["tool-card", "delegation-link", "tool-card"]);
  });

  it("draws nothing for a message with no calls", () => {
    expect(bubble([]).find(".msg-tools").exists()).toBe(false);
  });
});

describe("MessageBubble tool rows: screenshots", () => {
  it("adds a ToolScreenshot after the ToolCard when the call kept a screenshot", () => {
    const view = bubble([tool({ id: "t1", kind: "fleet_browser_screenshot", title: "Shop · 1280×800", screenshot: shot })]);

    expect(view.findAllComponents(ToolCard)).toHaveLength(1);
    const shotView = view.getComponent(ToolScreenshot);
    expect(shotView.props()).toEqual({ screenshot: shot, title: "Shop · 1280×800" });
    const order = view.findAll(".msg-tools > *").map((el) => el.element.tagName.toLowerCase());
    expect(order).toEqual(["details", "tool-screenshot-stub"]);
  });

  it("draws no screenshot for a delegation, even if the call carries one", () => {
    const view = bubble([tool({ id: "t1", kind: "task", delegation, screenshot: shot })]);

    expect(view.findAllComponents(ToolScreenshot)).toHaveLength(0);
  });

  it("keys the screenshot on the item, not the tool kind: any call with one gets a thumbnail", () => {
    const view = bubble([tool({ id: "t1", kind: "bash", screenshot: shot })]);

    expect(view.findAllComponents(ToolScreenshot)).toHaveLength(1);
  });
});

describe("MessageBubble tool rows: browser steps", () => {
  it.each(["execute", "fleet_browser_read", "fleet_browser_act"])("adds BrowserSteps under a %s call", (kind) => {
    const view = bubble([tool({ id: "t1", kind, status: "Running", callId: "call-9" })]);

    expect(view.findAllComponents(ToolCard)).toHaveLength(1);
    expect(view.getComponent(BrowserSteps).props()).toEqual({ sessionId: "ses-1", callId: "call-9", running: true });
  });

  it("says the steps are no longer running once the call is not Running", () => {
    const view = bubble([tool({ id: "t1", kind: "fleet_browser_act", status: "Completed", callId: "call-9" })]);

    expect(view.getComponent(BrowserSteps).props("running")).toBe(false);
  });

  it.each(["fleet_browser_open", "fleet_browser_screenshot", "fleet_app_start", "bash", "read", "task"])(
    "adds no BrowserSteps under a %s call",
    (kind) => {
      expect(bubble([tool({ id: "t1", kind, callId: "call-9" })]).findAllComponents(BrowserSteps)).toHaveLength(0);
    },
  );

  it("adds no BrowserSteps without a session id", () => {
    expect(bubble([tool({ id: "t1", kind: "execute", callId: "c" })], null).findAllComponents(BrowserSteps)).toHaveLength(0);
  });

  it("adds no BrowserSteps to a delegation", () => {
    expect(bubble([tool({ id: "t1", kind: "execute", delegation })]).findAllComponents(BrowserSteps)).toHaveLength(0);
  });

  it("matches the kind in any case: Execute gets them too", () => {
    expect(bubble([tool({ id: "t1", kind: "Execute", callId: "c" })]).findAllComponents(BrowserSteps)).toHaveLength(1);
  });
});

describe("MessageBubble tool rows: pages", () => {
  it("draws a call's page outside the .msg-tools box, after it", () => {
    const view = bubble([tool({ id: "t1", kind: "fleet_page_show", title: "CI test times", page })]);

    expect(view.findAllComponents(ToolCard)).toHaveLength(1);
    const shown = view.getComponent(ConversationPage);
    // A conversation page is not a mod page: it keeps its own address and "open in a new tab".
    expect(shown.props()).toEqual({ page, title: "CI test times", src: undefined, contained: false });
    expect(view.find(".msg-tools").element.contains(shown.element)).toBe(false);
  });

  it("draws one page per call that has one, in call order", () => {
    const other = { ...page, id: "pg_other", path: "/pages/pg_other/b.html", source: "/tmp/x/b.html" };
    const view = bubble([
      tool({ id: "t1", kind: "fleet_page_show", title: "First", page }),
      tool({ id: "t2", kind: "read" }),
      tool({ id: "t3", kind: "fleet_page_show", title: "Second", page: other }),
    ]);

    expect(view.findAllComponents(ConversationPage).map((p) => p.props("title"))).toEqual(["First", "Second"]);
  });

  it("draws no page for a delegation", () => {
    expect(bubble([tool({ id: "t1", kind: "task", delegation, page })]).findAllComponents(ConversationPage)).toHaveLength(0);
  });

  it("draws a page for any call that has one, whatever its kind", () => {
    expect(bubble([tool({ id: "t1", kind: "bash", page })]).findAllComponents(ConversationPage)).toHaveLength(1);
  });

  it("has no paging of its own: every call is drawn, however many", () => {
    const many = Array.from({ length: 25 }, (_, i) => tool({ id: `t${i}`, kind: "read", title: `f${i}.ts` }));

    expect(bubble(many).findAllComponents(ToolCard)).toHaveLength(25);
  });
});
