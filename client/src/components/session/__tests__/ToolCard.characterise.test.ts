import { mount } from "@vue/test-utils";
import { describe, expect, it } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import ToolCard from "@/components/session/ToolCard.vue";
import MessageToolList from "@/components/session/MessageToolList.vue";

/**
 * A tool row as it was before mods: with nothing contributed, today's DOM. Imports only what existed before mods, so it
 * passes before them and after them alike.
 */
const diffLines = [
  { type: "remove" as const, content: "-one", oldLineNumber: 1 },
  { type: "add" as const, content: "+two", newLineNumber: 1 },
  { type: "add" as const, content: "+three", newLineNumber: 2 },
];

const CASES: Record<string, Record<string, unknown>> = {
  "bash with output and preview": { kind: "Bash", title: "ls -la", output: "a\nb", preview: "└ a (2 lines)", summary: "Listed" },
  "edit with diff stats": { kind: "Edit", title: "src/a.ts", diffLines },
  running: { kind: "Bash", title: "sleep 5", status: "Running" },
  error: { kind: "Bash", title: "false", status: "Error", output: "boom" },
  stopped: { kind: "Bash", title: "sleep 5", status: "Stopped" },
  background: { kind: "Bash", title: "serve", status: "Background" },
  "show and improve buttons": { kind: "Bash", title: "skill", canvasId: "cv1", improvable: true, output: "x" },
  collapsed: { kind: "Bash", title: "ls", output: "x", initiallyCollapsed: true },
  open: { kind: "Bash", title: "ls", output: "x", initiallyCollapsed: false },
  empty: { kind: "Bash", title: "true" },
};

function card(props: Record<string, unknown>) {
  const pinia = createPinia();
  setActivePinia(pinia);
  return mount(ToolCard, { global: { plugins: [pinia] }, props: { id: "c1", title: "x", ...props }, attachTo: document.body });
}

/** The DOM without Vue's v-if placeholders, which come and go with the template's branches. */
const html = (w: { element: Element }) => w.element.outerHTML.replace(/<!--[\s\S]*?-->/g, "");

describe("ToolCard with no contribution", () => {
  for (const [name, props] of Object.entries(CASES)) {
    it(`draws ${name} as it always did`, () => {
      expect(html(card(props))).toMatchSnapshot();
    });
  }
});

describe("MessageToolList with no contribution", () => {
  it("draws a row per call as it always did", () => {
    const pinia = createPinia();
    setActivePinia(pinia);
    const tools = [
      { id: "t1", callId: "call-1", title: "ls -la", kind: "Bash", status: "Completed", summary: "", output: "a", diffLines: [], initiallyCollapsed: true, preview: "", isPatternTool: false },
      { id: "t2", callId: "call-2", title: "src/a.ts", kind: "Edit", status: "Completed", summary: "", output: "", diffLines, initiallyCollapsed: true, preview: "", isPatternTool: false },
    ];
    const w = mount(MessageToolList, { global: { plugins: [pinia] }, props: { tools: tools as never, sessionId: "s1" } });
    expect(html(w)).toMatchSnapshot();
  });
});
