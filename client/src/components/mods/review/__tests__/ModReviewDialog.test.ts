import { flushPromises, mount } from "@vue/test-utils";
import { defineComponent, h } from "vue";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ModCheckReport, ModDraft } from "@/lib/mods/kept";

const { checkDraftMock, store } = vi.hoisted(() => ({
  checkDraftMock: vi.fn(),
  store: { keep: vi.fn(), dismissKeepRequest: vi.fn() },
}));

vi.mock("@/components/ui/dialog", () => {
  const pass = (name: string) => defineComponent({ name, setup: (_p, { slots }) => () => h("div", slots.default?.()) });
  return {
    Dialog: defineComponent({ name: "DialogStub", props: { open: Boolean }, setup: (props, { slots }) => () => (props.open ? h("div", slots.default?.()) : null) }),
    DialogContent: pass("DialogContent"),
    DialogDescription: pass("DialogDescription"),
    DialogHeader: pass("DialogHeader"),
    DialogTitle: pass("DialogTitle"),
  };
});
vi.mock("@/lib/mods/kept-api", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/mods/kept-api")>()),
  checkDraft: checkDraftMock,
}));
vi.mock("@/stores/mods", () => ({ useModsStore: () => store }));
vi.mock("@/stores/machines", () => ({ useMachinesStore: () => ({ sessionTarget: () => ({ connection: null }) }) }));
vi.mock("@/stores/sessions", () => ({
  useSessionsStore: () => ({ sessionById: () => ({ session: { title: "Make test output readable" } }) }),
}));

import { ModsRequestError } from "@/lib/mods/kept-api";
import ModReviewDialog from "@/components/mods/review/ModReviewDialog.vue";

const draft = (overrides: Partial<ModDraft> = {}): ModDraft =>
  ({ sessionId: "s1", name: "test-chips", description: "Test runs as counts", version: "1", off: null, kept: null, ...overrides });

const testChips: ModCheckReport = {
  ok: true, name: "test-chips", version: "1", description: "", lines: 47, sha256: "a",
  hooks: [{ event: "ui.render", matcher: { component: ["ToolUse", "ToolResult"], props: { tool: ["bash", "shell"] } } }],
  calls: ["ui.resolve"], state: [], pages: [], errors: [], warnings: [],
};

const rich: ModCheckReport = {
  ...testChips,
  hooks: [{ event: "ui.render", matcher: { component: "ComposerBand" } }, { event: "ui.press" }, { event: "session.start" }],
  calls: ["store.set", "ui.toast", "session.title"], state: ["count"], pages: ["panel.html"],
};

const mountDialog = (value: ModDraft = draft()) => mount(ModReviewDialog, { props: { open: true, sessionId: "s1", draft: value } });
const find = (wrapper: ReturnType<typeof mountDialog>, id: string) => wrapper.find(`[data-testid=mod-review-${id}]`);
const settle = async (wrapper: ReturnType<typeof mountDialog>, id: string) => { await find(wrapper, id).trigger("click"); await flushPromises(); };

beforeEach(() => {
  checkDraftMock.mockReset().mockResolvedValue(testChips);
  store.keep.mockReset().mockResolvedValue({});
  store.dismissKeepRequest.mockReset().mockResolvedValue(undefined);
});

describe("ModReviewDialog", () => {
  it("asks 'Keep test-chips?' and lists what the worked example touches", async () => {
    const wrapper = mountDialog();
    expect(find(wrapper, "loading").exists()).toBe(true);
    await flushPromises();
    expect(wrapper.text()).toContain("Keep test-chips?");
    expect(wrapper.text()).toContain("v1 · 47 lines · written in “Make test output readable”");
    expect(wrapper.findAll("[data-testid=mod-review-row]").map((row) => row.text())).toEqual([
      "Draws" + "Rows for bash and shell calls (on the line, and the opened body)",
      "Reads" + "Tool input and output of those calls",
      "Changes" + "Nothing the agent sees",
      "Network" + "None",
      "Files" + "None",
    ]);
  });

  it("lists a mod with pages, state and several hooks", async () => {
    checkDraftMock.mockResolvedValue(rich);
    const wrapper = mountDialog();
    await flushPromises();
    for (const part of ["A band above the composer", "Buttons and fields it draws", "Keeps per-session state: count", "Saves data for you", "panel.html", "Shows notices"]) {
      expect(wrapper.text()).toContain(part);
    }
  });

  it("says so when there is no report, and Keep stays possible", async () => {
    checkDraftMock.mockResolvedValue(null);
    const wrapper = mountDialog();
    await flushPromises();
    expect(find(wrapper, "unchecked").text()).toContain("the mod runtime isn't running");
    expect(find(wrapper, "keep").attributes("disabled")).toBeUndefined();
  });

  it("blocks Keep on an error, and only warns on a warning", async () => {
    checkDraftMock.mockResolvedValue({ ...testChips, ok: false, errors: [{ line: 12, column: 3, code: "x", message: "Call on() inside register" }] });
    const wrapper = mountDialog();
    await flushPromises();
    expect(find(wrapper, "error").text()).toBe("Fix this first: Call on() inside register (line 12:3)");
    expect(find(wrapper, "keep").attributes("disabled")).toBeDefined();

    checkDraftMock.mockResolvedValue({ ...testChips, warnings: [{ code: "w", message: "Unused state key" }] });
    const warned = mountDialog();
    await flushPromises();
    expect(find(warned, "warnings").text()).toContain("1 warning");
    expect(find(warned, "keep").attributes("disabled")).toBeUndefined();
  });

  it("keeps with the note, and closes", async () => {
    const wrapper = mountDialog();
    await flushPromises();
    await find(wrapper, "note").setValue("Counts are easier to scan.");
    await settle(wrapper, "keep");
    expect(store.keep).toHaveBeenCalledWith("s1", "test-chips", "Counts are easier to scan.");
    expect(wrapper.emitted("update:open")?.at(-1)).toEqual([false]);
  });

  it("shows the server's words when Keep is refused, and stays open", async () => {
    store.keep.mockRejectedValue(new ModsRequestError("The check found a problem: line 3", 400));
    const wrapper = mountDialog();
    await flushPromises();
    await settle(wrapper, "keep");
    expect(find(wrapper, "refused").text()).toBe("The check found a problem: line 3");
    expect(wrapper.emitted("update:open")).toBeUndefined();
  });

  it("without a keep request, Cancel only closes", async () => {
    const wrapper = mountDialog();
    await flushPromises();
    expect(find(wrapper, "decline").text()).toBe("Cancel");
    await settle(wrapper, "decline");
    expect(store.dismissKeepRequest).not.toHaveBeenCalled();
    expect(wrapper.emitted("update:open")?.at(-1)).toEqual([false]);
  });

  it("with the agent's request, prefills the note and Discard tells the agent", async () => {
    const wrapper = mountDialog(draft({ keepRequest: { note: "Counts are easier to scan.", at: "2026-10-10T10:00:00Z" } }));
    await flushPromises();
    expect((find(wrapper, "note").element as HTMLTextAreaElement).value).toBe("Counts are easier to scan.");
    expect(wrapper.text()).toContain("Discard tells the agent you didn't keep it. The draft stays on in this session.");
    expect(find(wrapper, "decline").text()).toBe("Discard");
    await settle(wrapper, "decline");
    expect(store.dismissKeepRequest).toHaveBeenCalledWith("s1", "test-chips");
    expect(wrapper.emitted("update:open")?.at(-1)).toEqual([false]);
  });
});
