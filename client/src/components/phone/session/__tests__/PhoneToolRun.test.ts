import { mount } from "@vue/test-utils";
import { describe, expect, it } from "vitest";
import PhoneToolRun from "@/components/phone/session/PhoneToolRun.vue";
import { foldMessages, type FoldedStep, type PhoneBlock } from "@/lib/phone/fold-steps";
import type { AccumulatedMessage, AccumulatedToolPart } from "@/lib/client-types";

/** First test file for PhoneToolRun: what a run of tool calls looks like on the phone, built through foldMessages. */
type Steps = Extract<PhoneBlock, { kind: "steps" }>;
type Subagent = Extract<PhoneBlock, { kind: "subagent" }>;

function call(id: string, tool: string, status: string, input: Record<string, unknown> = {}, extra: Record<string, unknown> = {}): AccumulatedToolPart {
  return { type: "tool", partId: `p-${id}`, callId: `c-${id}`, tool, state: { status, input, ...extra } } as AccumulatedToolPart;
}

function fold(parts: AccumulatedToolPart[]): (Steps | Subagent)[] {
  const message = { messageId: "m1", role: "assistant", parts, createdAt: 1 } as unknown as AccumulatedMessage;
  return foldMessages([message]).filter((b): b is Steps | Subagent => b.kind === "steps" || b.kind === "subagent");
}

function run(parts: AccumulatedToolPart[], props: { waitingCalls?: ReadonlySet<string>; all?: boolean } = {}) {
  return mount(PhoneToolRun, { props: { parts: fold(parts), ...props } });
}

describe("PhoneToolRun rows", () => {
  it("draws a finished read as Read, its file and a check", () => {
    const row = run([call("1", "read", "completed", { filePath: "src/a.ts" })]).get("[data-testid='phone-step']");

    expect(row.get(".ph-tool__l").text()).toBe("Read");
    expect(row.get(".ph-tool__d").text()).toBe("src/a.ts");
    expect(row.find("[aria-label='Done']").exists()).toBe(true);
  });

  it("draws a finished edit with its line counts instead of the check", () => {
    const row = run([call("1", "edit", "completed", { filePath: "src/a.ts", oldString: "one", newString: "two\nthree" })]).get("[data-testid='phone-step']");

    expect(row.get(".ph-tool__l").text()).toBe("Edit");
    expect(row.get(".ph-tool__d").text()).toBe("src/a.ts");
    expect(row.get(".ph-add").text()).toBe("+2");
    expect(row.get(".ph-del").text()).toBe("−1");
    expect(row.find("[aria-label='Done']").exists()).toBe(false);
  });

  it("draws a run call with its command as the detail", () => {
    const row = run([call("1", "bash", "completed", { command: "ls -la" })]).get("[data-testid='phone-step']");

    expect(row.get(".ph-tool__l").text()).toBe("Bash");
    expect(row.get(".ph-tool__d").text()).toBe("ls -la");
  });

  it("draws a search pattern as a pill", () => {
    const row = run([call("1", "grep", "completed", { pattern: "TODO" })]).get("[data-testid='phone-step']");

    expect(row.get(".ph-tool__l").text()).toBe("Grep");
    expect(row.get(".ph-tool__pat").text()).toBe("TODO");
  });

  it("draws a search that is not a pattern tool as plain detail", () => {
    const row = run([call("1", "websearch", "completed", { query: "vue" })]).get("[data-testid='phone-step']");

    expect(row.get(".ph-tool__l").text()).toBe("Web Search");
    expect(row.find(".ph-tool__pat").exists()).toBe(false);
  });

  it("draws an unknown tool by its capitalised name", () => {
    const row = run([call("1", "frobnicate", "completed", {})]).get("[data-testid='phone-step']");

    expect(row.get(".ph-tool__l").text()).toBe("Frobnicate");
    expect(row.find("[aria-label='Done']").exists()).toBe(true);
  });

  it.each(["running", "pending"])("draws a %s call with the running glyph", (status) => {
    const row = run([call("1", "read", status, { filePath: "a.ts" })]).get("[data-testid='phone-step']");

    expect(row.find("[aria-label='Running']").exists()).toBe(true);
    expect(row.find("[aria-label='Done']").exists()).toBe(false);
  });

  it("draws an error as the word failed, even for an edit", () => {
    const row = run([call("1", "edit", "error", { filePath: "a.ts", oldString: "a", newString: "b" })]).get("[data-testid='phone-step']");

    expect(row.get(".ph-tool__bad").text()).toBe("failed");
    expect(row.find(".ph-add").exists()).toBe(false);
  });

  it("draws Needs you for a call waiting on an ask, in place of its status", () => {
    const view = run([call("1", "bash", "running", { command: "rm -rf build" })], { waitingCalls: new Set(["c-1"]) });
    const row = view.get("[data-testid='phone-step']");

    expect(row.get(".ph-tool__needs").text()).toBe("Needs you");
    expect(row.find("[aria-label='Running']").exists()).toBe(false);
  });

  it("emits open with the step and its whole run when a row is tapped", async () => {
    const view = run([call("1", "read", "completed", { filePath: "a.ts" }), call("2", "read", "completed", { filePath: "b.ts" })]);

    await view.findAll("[data-testid='phone-step']")[1]!.trigger("click");

    const [step, steps] = view.emitted("open")![0] as [FoldedStep, FoldedStep[]];
    expect(step.id).toBe("p-2");
    expect(steps.map((s) => s.id)).toEqual(["p-1", "p-2"]);
  });
});

describe("PhoneToolRun folding", () => {
  const five = ["a", "b", "c", "d", "e"].map((n) => call(n, "read", "completed", { filePath: `${n}.ts` }));

  it("shows the first three of a long run and a row for the rest", async () => {
    const view = run(five);

    expect(view.findAll("[data-testid='phone-step']")).toHaveLength(3);
    const more = view.get("[data-testid='phone-steps-more']");
    expect(more.text()).toBe("2 more steps");
    await more.trigger("click");
    expect((view.emitted("more")![0]![0] as FoldedStep[]).map((s) => s.id)).toEqual(["p-d", "p-e"]);
  });

  it("shows four calls whole: it never says '1 more step'", () => {
    const view = run(five.slice(0, 4));

    expect(view.findAll("[data-testid='phone-step']")).toHaveLength(4);
    expect(view.find("[data-testid='phone-steps-more']").exists()).toBe(false);
  });

  it("shows every call when told to show all", () => {
    const view = run(five, { all: true });

    expect(view.findAll("[data-testid='phone-step']")).toHaveLength(5);
    expect(view.find("[data-testid='phone-steps-more']").exists()).toBe(false);
  });
});

describe("PhoneToolRun subagents", () => {
  const task = (status: string, metadata: Record<string, unknown> = {}) =>
    call("s", "task", status, { description: "Map the entry points", subagent_type: "explore" }, { metadata });

  it("draws a running subagent with its agent, its task and the working dots", () => {
    const row = run([task("running", { sessionId: "child-1" })]).get("[data-testid='phone-subagent']");

    expect(row.get(".ph-tool__l").text()).toBe("explore");
    expect(row.get(".ph-tool__d").text()).toBe("Map the entry points");
    expect(row.find("[aria-label='Working']").exists()).toBe(true);
  });

  it("draws a finished subagent with a chevron when its session is known, and opens it", async () => {
    const view = run([task("completed", { sessionId: "child-1" })]);
    const row = view.get("[data-testid='phone-subagent']");

    expect(row.find("[aria-label='Open']").exists()).toBe(true);
    await row.trigger("click");
    expect(view.emitted("child")).toEqual([["child-1"]]);
  });

  it("draws a finished subagent with no session as a check on a disabled row", async () => {
    const view = run([task("completed")]);
    const row = view.get("[data-testid='phone-subagent']");

    expect(row.find("[aria-label='Done']").exists()).toBe(true);
    expect(row.attributes("disabled")).toBeDefined();
    await row.trigger("click");
    expect(view.emitted("child")).toBeUndefined();
  });

  it("calls a subagent that names no kind 'agent'", () => {
    const row = run([call("s", "subagent", "completed", { description: "Do it" })]).get("[data-testid='phone-subagent']");

    expect(row.get(".ph-tool__l").text()).toBe("agent");
  });

  it("splits a run around a subagent into two boxes of steps and the subagent row between", () => {
    const view = run([call("1", "read", "completed", { filePath: "a.ts" }), task("running"), call("2", "bash", "completed", { command: "ls" })]);

    expect(view.findAll(".ph-tool").map((row) => row.attributes("data-testid"))).toEqual(["phone-step", "phone-subagent", "phone-step"]);
  });
});
