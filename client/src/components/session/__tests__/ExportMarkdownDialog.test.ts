import { flushPromises, mount } from "@vue/test-utils";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { AccumulatedMessage } from "@/lib/client-types";

const loadWholeHistory = vi.fn();
vi.mock("@/lib/export-history", () => ({
  loadWholeHistory: (...args: unknown[]) => loadWholeHistory(...args),
}));

import ExportMarkdownDialog from "@/components/session/ExportMarkdownDialog.vue";

// The dialog renders in a portal; the stubs render it inline.
const stubs = {
  Dialog: { props: ["open"], template: "<div v-if=\"open\"><slot /></div>" },
  DialogContent: { template: "<div><slot /></div>" },
  DialogTitle: { template: "<h2><slot /></h2>" },
  DialogDescription: { template: "<p><slot /></p>" },
  MarkdownRenderer: { props: ["content"], template: "<div data-testid=\"export-rendered\">{{ content }}</div>" },
};

const chat = (withReasoning: boolean): AccumulatedMessage[] => [
  { messageId: "m1", sessionId: "s1", role: "user", parts: [{ partId: "a", type: "text", text: "Add retries" }] },
  {
    messageId: "m2", sessionId: "s1", role: "assistant",
    parts: [
      ...(withReasoning ? [{ partId: "r", type: "reasoning" as const, text: "think" }] : []),
      { partId: "t", type: "tool" as const, tool: "bash", callId: "c", state: { status: "completed", input: { command: "env" }, output: "API_KEY=abcdef123456789" } },
      { partId: "b", type: "text" as const, text: "Done." },
    ],
  },
];

function mountDialog() {
  return mount(ExportMarkdownDialog, {
    props: { open: true, sessionId: "s1", title: "Add upload retries", directory: "/work/app" },
    global: { stubs },
  });
}

describe("ExportMarkdownDialog", () => {
  let writeText: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    loadWholeHistory.mockReset();
    writeText = vi.fn().mockResolvedValue(undefined);
    vi.stubGlobal("navigator", { ...navigator, clipboard: { writeText } });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it("shows how many messages it has loaded, then the file", async () => {
    let report: (count: number) => void = () => {};
    let finish: (value: AccumulatedMessage[]) => void = () => {};
    loadWholeHistory.mockImplementation((_id: string, onProgress: (count: number) => void) => {
      report = onProgress;
      return new Promise((resolve) => { finish = resolve; });
    });
    const wrapper = mountDialog();
    report(120);
    await flushPromises();
    expect(wrapper.get("[data-testid='export-loading']").text()).toContain("120 messages");
    expect(wrapper.get("[data-testid='export-copy']").attributes("disabled")).toBeDefined();

    finish(chat(false));
    await flushPromises();
    expect(wrapper.get("[data-testid='export-source']").text()).toContain("## You");
    expect(wrapper.get("[data-testid='export-size']").text()).toContain("2 turns");
  });

  it("says so, with a way to try again, when a page fails", async () => {
    loadWholeHistory.mockRejectedValueOnce(new Error("Fleet could not load the conversation (error 502 after 200 messages)."));
    const wrapper = mountDialog();
    await flushPromises();
    expect(wrapper.get("[data-testid='export-error']").text()).toContain("error 502 after 200 messages");

    loadWholeHistory.mockResolvedValueOnce(chat(false));
    await wrapper.get("[data-testid='export-error'] button").trigger("click");
    await flushPromises();
    expect(wrapper.find("[data-testid='export-error']").exists()).toBe(false);
    expect(wrapper.find("[data-testid='export-source']").exists()).toBe(true);
  });

  it("changes the file at once when tool output is switched on, and warns about keys", async () => {
    loadWholeHistory.mockResolvedValue(chat(false));
    const wrapper = mountDialog();
    await flushPromises();
    expect(wrapper.get("[data-testid='export-source']").text()).not.toContain("API_KEY");
    expect(wrapper.find("[data-testid='export-output-warning']").exists()).toBe(false);

    await wrapper.get("input[value='output']").setValue(true);
    expect(wrapper.get("[data-testid='export-source']").text()).toContain("API_KEY=abcdef123456789");
    expect(wrapper.get("[data-testid='export-output-warning']").text()).toContain("Fleet does not remove them");
    expect(wrapper.get("[data-testid='export-key-lines']").text()).toContain("1 line looks like a key");
  });

  it("turns Thinking off, with the reason, when the session has none", async () => {
    loadWholeHistory.mockResolvedValue(chat(false));
    const wrapper = mountDialog();
    await flushPromises();
    expect(wrapper.get("[data-testid='export-thinking']").attributes("disabled")).toBeDefined();
    expect(wrapper.text()).toContain("This session has no thinking to include.");

    loadWholeHistory.mockResolvedValue(chat(true));
    const again = mountDialog();
    await flushPromises();
    expect(again.get("[data-testid='export-thinking']").attributes("disabled")).toBeUndefined();
  });

  it("copies the Markdown on Copy", async () => {
    loadWholeHistory.mockResolvedValue(chat(false));
    const wrapper = mountDialog();
    await flushPromises();

    await wrapper.get("[data-testid='export-copy']").trigger("click");
    await flushPromises();

    expect(writeText).toHaveBeenCalledOnce();
    expect(writeText.mock.calls[0][0]).toContain("# Add upload retries");
    expect(wrapper.get("[data-testid='export-copy']").text()).toBe("Copied");
  });

  it("saves <title>.md on Save", async () => {
    loadWholeHistory.mockResolvedValue(chat(false));
    let blob: Blob | undefined;
    URL.createObjectURL = vi.fn((value: Blob) => { blob = value; return "blob:fake"; });
    URL.revokeObjectURL = vi.fn();
    const downloads: string[] = [];
    vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(function (this: HTMLAnchorElement) {
      downloads.push(this.download);
    });
    const wrapper = mountDialog();
    await flushPromises();

    await wrapper.get("[data-testid='export-save']").trigger("click");

    expect(downloads).toEqual(["add-upload-retries.md"]);
    expect(await blob!.text()).toContain("# Add upload retries");
    expect(URL.revokeObjectURL).toHaveBeenCalledWith("blob:fake");
  });

  it("switches to the rendered preview", async () => {
    loadWholeHistory.mockResolvedValue(chat(false));
    const wrapper = mountDialog();
    await flushPromises();

    await wrapper.findAll("button").find((button) => button.text() === "Preview")!.trigger("click");

    expect(wrapper.find("[data-testid='export-source']").exists()).toBe(false);
    expect(wrapper.get("[data-testid='export-rendered']").text()).toContain("# Add upload retries");
  });
});
