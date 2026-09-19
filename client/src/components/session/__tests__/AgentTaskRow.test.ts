import { mount } from "@vue/test-utils";
import { describe, expect, it, vi } from "vitest";
import type { ToolCardDelegation } from "@/components/session/activity-stream-tool-card";
import { subagentKind, subagentTask } from "@/components/session/activity-stream-tool-card";

const navigateMock = vi.fn();
vi.mock("@tanstack/vue-router", () => ({ useRouter: () => ({ navigate: navigateMock }) }));

const { default: AgentTaskRow } = await import("@/components/session/AgentTaskRow.vue");

function delegation(overrides: Partial<ToolCardDelegation> = {}): ToolCardDelegation {
  return {
    href: "/sessions/child-1?instanceId=inst-1&parentSessionId=parent-1",
    childSessionId: "child-1",
    childInstanceId: "inst-1",
    parentSessionId: "parent-1",
    agent: "explore",
    task: "Map every archive entry point",
    status: "running",
    ...overrides,
  };
}

describe("AgentTaskRow", () => {
  it("names the agent and its task", () => {
    const wrapper = mount(AgentTaskRow, { props: { delegation: delegation() } });

    expect(wrapper.text()).toContain("explore");
    expect(wrapper.get("[data-testid='delegation-link-title']").text()).toBe("Map every archive entry point");
    expect(wrapper.get("a").attributes("href")).toBe("/sessions/child-1?instanceId=inst-1&parentSessionId=parent-1");
  });

  it.each([
    ["running", "Working", true],
    ["pending", "Starting", true],
    ["completed", "Done", false],
    ["error", "Failed", false],
    ["cancelled", "Cancelled", false],
  ])("shows a %s child as %s", (status, word, working) => {
    const wrapper = mount(AgentTaskRow, { props: { delegation: delegation({ status }) } });

    expect(wrapper.get("[data-testid='delegation-link-status']").text()).toBe(word);
    expect(wrapper.find(".status-glyph--working").exists()).toBe(working);
  });

  it("opens the child session in place", async () => {
    navigateMock.mockClear();
    const wrapper = mount(AgentTaskRow, { props: { delegation: delegation() } });

    await wrapper.get("a").trigger("click", { button: 0 });

    expect(navigateMock).toHaveBeenCalledWith({
      to: "/sessions/$id",
      params: { id: "child-1" },
      search: { instanceId: "inst-1", parentSessionId: "parent-1" },
    });
  });

  it("leaves a modified click to the browser", async () => {
    navigateMock.mockClear();
    const wrapper = mount(AgentTaskRow, { props: { delegation: delegation() } });

    await wrapper.get("a").trigger("click", { button: 0, metaKey: true });

    expect(navigateMock).not.toHaveBeenCalled();
  });
});

describe("sub-agent call details", () => {
  const call = (tool: string, input: Record<string, unknown>) =>
    ({ partId: "p", type: "tool" as const, tool, callId: "c", state: { status: "running", input } });

  it("reads OpenCode's task input", () => {
    const part = call("task", { subagent_type: "explore", description: "Find the auth middleware", prompt: "…" });

    expect(subagentKind(part)).toBe("explore");
    expect(subagentTask(part)).toBe("Find the auth middleware");
  });

  it("reads OpenCode 2's subagent input", () => {
    const part = call("subagent", { agent: "general", description: "Summarise the diff" });

    expect(subagentKind(part)).toBe("general");
    expect(subagentTask(part)).toBe("Summarise the diff");
  });

  it("is empty when the call doesn't say", () => {
    const part = call("task", {});

    expect(subagentKind(part)).toBe("");
    expect(subagentTask(part)).toBe("");
  });
});
