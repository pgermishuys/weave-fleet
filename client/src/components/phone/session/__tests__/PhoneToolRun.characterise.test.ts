import { mount } from "@vue/test-utils";
import { describe, expect, it } from "vitest";
import { defineComponent, h } from "vue";
import PhoneToolRun from "@/components/phone/session/PhoneToolRun.vue";
import StepsSheet from "@/components/phone/session/StepsSheet.vue";
import { foldMessages, type FoldedStep, type PhoneBlock } from "@/lib/phone/fold-steps";
import type { AccumulatedMessage, AccumulatedToolPart } from "@/lib/client-types";

/**
 * A phone tool row and the steps sheet as they were before mods: with nothing contributed, today's DOM. Imports only
 * what existed before mods, so it passes before them and after them alike.
 */
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
  return mount(PhoneToolRun, { props: { parts: fold(parts), ...props } });
}

const BottomSheet = defineComponent({ setup: (_, { slots }) => () => h("div", [slots.head?.(), slots.default?.()]) });
function sheet(parts: AccumulatedToolPart[]) {
  const steps = (fold(parts)[0] as Steps).steps as FoldedStep[];
  return mount(StepsSheet, { props: { open: true, steps, focus: steps[0] }, global: { stubs: { BottomSheet } } });
}

/** The DOM without Vue's v-if placeholders, which come and go with the template's branches. */
const html = (w: { element: Element }) => w.element.outerHTML.replace(/<!--[\s\S]*?-->/g, "");

const edit = call("1", "edit", "completed", { filePath: "src/a.ts", oldString: "one", newString: "two\nthree" });
const ROWS: Record<string, AccumulatedToolPart[]> = {
  "done read": [call("1", "read", "completed", { filePath: "src/a.ts" })],
  "edit with diff": [edit],
  failed: [call("1", "bash", "error", { command: "false" })],
  running: [call("1", "bash", "running", { command: "sleep 5" })],
  "more steps": [1, 2, 3, 4, 5].map((n) => call(String(n), "read", "completed", { filePath: `src/${n}.ts` })),
};

describe("PhoneToolRun with no contribution", () => {
  for (const [name, parts] of Object.entries(ROWS)) {
    it(`draws ${name} as it always did`, () => {
      expect(html(run(parts))).toMatchSnapshot();
    });
  }
  it("draws a row that needs you as it always did", () => {
    expect(html(run([call("1", "bash", "running", { command: "rm x" })], { waitingCalls: new Set(["c-1"]) }))).toMatchSnapshot();
  });
  it("draws a subagent row as it always did", () => {
    const parts = [call("1", "task", "completed", { description: "look", subagent_type: "explore" }, { metadata: { sessionId: "child-1" } })];
    expect(html(run(parts))).toMatchSnapshot();
  });
});

describe("StepsSheet with no contribution", () => {
  it("draws an opened command as it always did", () => {
    expect(html(sheet([call("1", "bash", "completed", { command: "ls" }, { output: "a\nb" })]))).toMatchSnapshot();
  });
  it("draws an opened edit as it always did", () => {
    expect(html(sheet([edit]))).toMatchSnapshot();
  });
});

