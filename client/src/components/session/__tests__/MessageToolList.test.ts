import { mount } from "@vue/test-utils";
import { afterEach, describe, expect, it, vi } from "vitest";
import { createPinia } from "pinia";
import type { ToolCardItem } from "@/components/session/activity-stream-tool-card";

vi.mock("@tanstack/vue-router", () => ({ useRouter: () => ({ navigate: vi.fn() }) }));

const { default: MessageToolList } = await import("@/components/session/MessageToolList.vue");
const { default: ToolCard } = await import("@/components/session/ToolCard.vue");
const { default: AgentTaskRow } = await import("@/components/session/AgentTaskRow.vue");
const { default: BrowserSteps } = await import("@/components/session/BrowserSteps.vue");
const { default: ConversationPage } = await import("@/components/session/ConversationPage.vue");

let wrapper: { unmount(): void } | undefined;
afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
});

function tool(overrides: Partial<ToolCardItem> & { id: string }): ToolCardItem {
  return { title: "A call", kind: "bash", status: "Completed", initiallyCollapsed: true, ...overrides };
}

function list(tools: ToolCardItem[] | undefined, sessionId: string | null = "ses-1") {
  const mounted = mount(MessageToolList, {
    global: { plugins: [createPinia()], stubs: { BrowserSteps: true, ConversationPage: true, ToolScreenshot: true } },
    props: { tools, sessionId: sessionId ?? undefined },
  });
  wrapper = mounted;
  return mounted;
}

const page = { path: "/pages/pg_0123456789abcdef0123456789abcdef/times.html", id: "pg_0123456789abcdef0123456789abcdef", source: "/tmp/x/times.html" };

describe("MessageToolList", () => {
  it("draws nothing without calls", () => {
    expect(list(undefined).find(".msg-tools").exists()).toBe(false);
    expect(list([]).find(".msg-tools").exists()).toBe(false);
  });

  it("draws a ToolCard per call inside the box", () => {
    const view = list([tool({ id: "a", kind: "read" }), tool({ id: "b", kind: "grep" })]);

    expect(view.findAll(".msg-tools > *")).toHaveLength(2);
    expect(view.findAllComponents(ToolCard).map((card) => card.props("kind"))).toEqual(["read", "grep"]);
  });

  it("draws a delegated call as an AgentTaskRow", () => {
    const delegation = { href: "/sessions/c", childSessionId: "c", childInstanceId: "i", parentSessionId: "p", agent: "explore", task: "Look", status: "running" };
    const view = list([tool({ id: "a", kind: "task", delegation })]);

    expect(view.findAllComponents(AgentTaskRow)).toHaveLength(1);
    expect(view.findAllComponents(ToolCard)).toHaveLength(0);
  });

  it("adds browser steps to a Code Mode call, only with a session", () => {
    expect(list([tool({ id: "a", kind: "execute" })]).findAllComponents(BrowserSteps)).toHaveLength(1);
    expect(list([tool({ id: "a", kind: "execute" })], null).findAllComponents(BrowserSteps)).toHaveLength(0);
    expect(list([tool({ id: "a", kind: "bash" })]).findAllComponents(BrowserSteps)).toHaveLength(0);
  });

  it("draws a call's page after the box, not inside it", () => {
    const view = list([tool({ id: "a", page })]);

    expect(view.findAllComponents(ConversationPage)).toHaveLength(1);
    expect(view.find(".msg-tools").findComponent(ConversationPage).exists()).toBe(false);
  });

  it("passes a row's events up with the call's title and id", async () => {
    const view = list([tool({ id: "a", title: "frontend-design", kind: "skill", improvable: true })]);
    const card = view.getComponent(ToolCard);

    card.vm.$emit("improve");
    card.vm.$emit("show-canvas", "cv_1");

    expect(view.emitted("improve-skill")).toEqual([["frontend-design", "a"]]);
    expect(view.emitted("show-canvas")).toEqual([["cv_1"]]);
  });
});
