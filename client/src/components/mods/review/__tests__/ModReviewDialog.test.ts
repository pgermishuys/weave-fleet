import { flushPromises, mount } from "@vue/test-utils";
import { defineComponent, h } from "vue";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ModCheckReport, ModDraft } from "@/lib/mods/kept";

const { checkDraftMock, store } = vi.hoisted(() => ({
  checkDraftMock: vi.fn(),
  store: { keep: vi.fn(), setDraftOn: vi.fn(), dismissKeepRequest: vi.fn() },
}));

vi.mock("@/components/ui/dialog", () => {
  const pass = (name: string) => defineComponent({ name, setup: (_p, { slots }) => () => h("div", slots.default?.()) });
  return {
    Dialog: defineComponent({ name: "DialogStub", props: { open: Boolean }, setup: (props, { slots }) => () => (props.open ? h("div", slots.default?.()) : null) }),
    DialogContent: pass("DialogContent"),
    DialogDescription: pass("DialogDescription"),
    DialogHeader: pass("DialogHeader"),
    DialogFooter: pass("DialogFooter"),
    DialogTitle: pass("DialogTitle"),
  };
});
vi.mock("@/lib/mods/kept-api", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/mods/kept-api")>()),
  checkDraft: checkDraftMock,
}));
vi.mock("@/stores/mods", () => ({ useModsStore: () => store }));
vi.mock("@/stores/machines", () => ({
  useMachinesStore: () => ({ sessionTarget: () => ({ connection: null }) }),
}));
vi.mock("@/stores/sessions", () => ({
  useSessionsStore: () => ({ sessionById: (id: string) => (id === "s1" ? { session: { title: "Make test output readable" } } : undefined) }),
}));
vi.mock("@/components/phone/BottomSheet.vue", () => ({
  default: defineComponent({
    name: "BottomSheetStub",
    props: { open: Boolean, label: String, title: String },
    emits: ["close"],
    setup: (props, { slots }) => () => (props.open ? h("div", { "data-testid": "sheet", "data-label": props.label }, [slots.default?.(), h("div", { "data-testid": "sheet-foot" }, slots.foot?.())]) : null),
  }),
}));
vi.mock("@/components/mods/review/ModCodeDialog.vue", () => ({
  default: defineComponent({ name: "ModCodeDialog", props: ["open", "title", "load"], setup: (p) => () => h("div", { "data-testid": "code", "data-open": String(p.open) }) }),
}));

import { ModsRequestError } from "@/lib/mods/kept-api";
import ModReviewDialog from "@/components/mods/review/ModReviewDialog.vue";

function draft(overrides: Partial<ModDraft> = {}): ModDraft {
  return { sessionId: "s1", name: "test-chips", description: "Test runs as counts", version: "1", off: null, kept: null, ...overrides };
}

const testChips: ModCheckReport = {
  ok: true, name: "test-chips", version: "1", description: "Test runs as counts", lines: 47, sha256: "a",
  hooks: [{ event: "ui.render", matcher: { component: ["ToolUse", "ToolResult"], props: { tool: ["bash", "shell"] } } }],
  calls: ["ui.resolve"], state: [], pages: [], errors: [], warnings: [],
};

const busy: ModCheckReport = {
  ...testChips, lines: 210,
  hooks: [
    { event: "ui.render", matcher: { component: "ComposerBand" } },
    { event: "ui.render", matcher: { component: "StatusChip" } },
    { event: "ui.render", matcher: { component: "Pane" } },
    { event: "ui.press" },
    { event: "session.start" },
  ],
  calls: ["store.get", "store.set", "ui.open", "ui.toast", "clock.every", "state.get", "state.set", "session.title"],
  state: ["count", "open"], pages: ["panel.html"],
};

function mountDialog(value: ModDraft = draft(), phone = false) {
  return mount(ModReviewDialog, { props: { open: true, sessionId: "s1", draft: value, phone } });
}

const rows = (wrapper: ReturnType<typeof mountDialog>) =>
  wrapper.findAll("[data-testid=mod-review-row]").map((row) => row.text());

beforeEach(() => {
  checkDraftMock.mockReset().mockResolvedValue(testChips);
  store.keep.mockReset().mockResolvedValue({});
  store.setDraftOn.mockReset().mockResolvedValue({});
  store.dismissKeepRequest.mockReset().mockResolvedValue(undefined);
});

describe("ModReviewDialog", () => {
  it("asks 'Keep test-chips?' with the version, lines and session", async () => {
    const wrapper = mountDialog();
    expect(wrapper.find("[data-testid=mod-review-loading]").exists()).toBe(true);
    await flushPromises();
    expect(wrapper.text()).toContain("Keep test-chips?");
    expect(wrapper.find("[data-testid=mod-review-meta]").text()).toBe('v1 · 47 lines · written in “Make test output readable”');
  });

  it("numbers the next version from the kept one", async () => {
    const wrapper = mountDialog(draft({ kept: 2 }));
    await flushPromises();
    expect(wrapper.find("[data-testid=mod-review-meta]").text()).toContain("v3 ·");
  });

  it("lists what the worked example touches", async () => {
    const wrapper = mountDialog();
    await flushPromises();
    expect(rows(wrapper)).toEqual([
      expect.stringContaining("Rows for bash and shell calls (on the line, and the opened body)"),
      expect.stringContaining("Tool input and output of those calls"),
      expect.stringContaining("Nothing the agent sees"),
      expect.stringContaining("None"),
      expect.stringContaining("None"),
    ]);
  });

  it("lists a mod with pages, state and many hooks", async () => {
    checkDraftMock.mockResolvedValue(busy);
    const wrapper = mountDialog();
    await flushPromises();
    const text = rows(wrapper).join("\n");
    for (const part of [
      "A band above the composer", "A chip in the status bar", "A pane it opens", "Buttons and fields it draws",
      "Session details (title, folder, harness)", "Keeps per-session state: count, open",
      "Saves data for you on this machine", "Shows its own pages: panel.html", "Shows notices, opens panes, runs timers",
    ]) expect(text).toContain(part);
  });

  it("says so when the check isn't available, and Keep stays possible", async () => {
    checkDraftMock.mockResolvedValue(null);
    const wrapper = mountDialog();
    await flushPromises();
    expect(wrapper.text()).toContain("Fleet couldn't check this mod yet: the mod runtime isn't running. Keep will check it.");
    expect(wrapper.find("[data-testid=mod-review-keep]").attributes("disabled")).toBeUndefined();
  });

  it("blocks Keep on an error and says to fix it first", async () => {
    checkDraftMock.mockResolvedValue({ ...testChips, ok: false, errors: [{ line: 12, column: 3, code: "x", message: "Call on() inside register" }] });
    const wrapper = mountDialog();
    await flushPromises();
    expect(wrapper.find("[data-testid=mod-review-error]").text()).toBe("Fix this first: Call on() inside register (line 12:3)");
    expect(wrapper.find("[data-testid=mod-review-keep]").attributes("disabled")).toBeDefined();
  });

  it("shows warnings without blocking", async () => {
    checkDraftMock.mockResolvedValue({ ...testChips, warnings: [{ code: "w", message: "Unused state key" }, { code: "w", message: "Other" }] });
    const wrapper = mountDialog();
    await flushPromises();
    expect(wrapper.find("[data-testid=mod-review-warnings]").text()).toContain("2 warnings");
    expect(wrapper.find("[data-testid=mod-review-keep]").attributes("disabled")).toBeUndefined();
  });

  it("keeps with the note, closes and emits kept", async () => {
    const wrapper = mountDialog();
    await flushPromises();
    await wrapper.find("[data-testid=mod-review-note]").setValue("Counts are easier to scan.");
    await wrapper.find("[data-testid=mod-review-keep]").trigger("click");
    await flushPromises();
    expect(store.keep).toHaveBeenCalledWith("s1", "test-chips", "Counts are easier to scan.");
    expect(wrapper.emitted("kept")).toHaveLength(1);
    expect(wrapper.emitted("update:open")?.at(-1)).toEqual([false]);
  });

  it("limits the note to 2000 characters", async () => {
    const wrapper = mountDialog();
    await flushPromises();
    expect(wrapper.find("[data-testid=mod-review-note]").attributes("maxlength")).toBe("2000");
  });

  it("shows the server's words when Keep is refused, and stays open", async () => {
    store.keep.mockRejectedValue(new ModsRequestError("The check found a problem: line 3", 400));
    const wrapper = mountDialog();
    await flushPromises();
    await wrapper.find("[data-testid=mod-review-keep]").trigger("click");
    await flushPromises();
    expect(wrapper.find("[data-testid=mod-review-refused]").text()).toBe("The check found a problem: line 3");
    expect(wrapper.emitted("kept")).toBeUndefined();
    expect(wrapper.emitted("update:open")).toBeUndefined();
  });

  it("shows a code row for a matcher it can't put in words, as code and not as a string", async () => {
    checkDraftMock.mockResolvedValue({ ...testChips, hooks: [{ event: "ui.render", matcher: { component: "Mystery" } }] });
    const wrapper = mountDialog();
    await flushPromises();
    expect(wrapper.find("[data-testid=mod-review-row] code").text()).toBe('{"component":"Mystery"}');
    expect(wrapper.find("[data-testid=mod-review-row]").text()).not.toContain("`");
  });

  it("keeps the actions in a footer outside the scrolling body", async () => {
    const wrapper = mountDialog();
    await flushPromises();
    const foot = wrapper.find("[data-testid=mod-review-foot]");
    expect(foot.find("[data-testid=mod-review-keep]").exists()).toBe(true);
    expect(foot.find("[data-testid=mod-review-decline]").exists()).toBe(true);
    expect(wrapper.find("[data-testid=mod-review-body]").find("[data-testid=mod-review-keep]").exists()).toBe(false);
  });

  it("opens Show code", async () => {
    const wrapper = mountDialog();
    await flushPromises();
    await wrapper.find("[data-testid=mod-review-code]").trigger("click");
    expect(wrapper.find("[data-testid=code]").attributes("data-open")).toBe("true");
  });

  describe("without a keep request", () => {
    it("Cancel only closes", async () => {
      const wrapper = mountDialog();
      await flushPromises();
      expect(wrapper.find("[data-testid=mod-review-decline]").text()).toBe("Cancel");
      expect(wrapper.text()).not.toContain("Discard tells the agent");
      await wrapper.find("[data-testid=mod-review-decline]").trigger("click");
      await flushPromises();
      expect(store.dismissKeepRequest).not.toHaveBeenCalled();
      expect(store.setDraftOn).not.toHaveBeenCalled();
      expect(wrapper.emitted("update:open")?.at(-1)).toEqual([false]);
    });
  });

  describe("with the agent's keep request", () => {
    const asked = draft({ keepRequest: { note: "Counts are easier to scan.", at: "2026-10-10T10:00:00Z" } });

    it("prefills the note", async () => {
      const wrapper = mountDialog(asked);
      await flushPromises();
      expect((wrapper.find("[data-testid=mod-review-note]").element as HTMLTextAreaElement).value).toBe("Counts are easier to scan.");
    });

    it("Discard tells the agent, leaves the draft on and closes", async () => {
      const wrapper = mountDialog(asked);
      await flushPromises();
      expect(wrapper.text()).toContain("Discard tells the agent you didn't keep it. The draft stays on in this session.");
      expect(wrapper.find("[data-testid=mod-review-decline]").text()).toBe("Discard");
      await wrapper.find("[data-testid=mod-review-decline]").trigger("click");
      await flushPromises();
      expect(store.dismissKeepRequest).toHaveBeenCalledWith("s1", "test-chips");
      expect(store.setDraftOn).not.toHaveBeenCalled();
      expect(wrapper.emitted("update:open")?.at(-1)).toEqual([false]);
    });

    it("stays open and says why when the dismissal fails", async () => {
      store.dismissKeepRequest.mockRejectedValue(new ModsRequestError("That draft is gone.", 404));
      const wrapper = mountDialog(asked);
      await flushPromises();
      await wrapper.find("[data-testid=mod-review-decline]").trigger("click");
      await flushPromises();
      expect(wrapper.find("[data-testid=mod-review-refused]").text()).toBe("That draft is gone.");
      expect(wrapper.emitted("update:open")).toBeUndefined();
    });
  });

  describe("on the phone", () => {
    it("is a sheet with the phone's buttons in its footer", async () => {
      const wrapper = mountDialog(draft(), true);
      await flushPromises();
      expect(wrapper.find("[data-testid=sheet]").exists()).toBe(true);
      const foot = wrapper.find("[data-testid=sheet-foot]");
      const keep = foot.find("[data-testid=mod-review-keep]");
      expect(keep.classes()).toEqual(expect.arrayContaining(["ph-btn", "ph-btn--primary"]));
      expect(foot.find("[data-testid=mod-review-code]").classes()).toContain("ph-btn--outline");
      expect(foot.find("[data-testid=mod-review-decline]").exists()).toBe(true);
      expect(wrapper.find("[data-slot=button]").exists()).toBe(false);
    });

    it("keeps working: Keep calls the store", async () => {
      const wrapper = mountDialog(draft(), true);
      await flushPromises();
      await wrapper.find("[data-testid=mod-review-keep]").trigger("click");
      await flushPromises();
      expect(store.keep).toHaveBeenCalledWith("s1", "test-chips", "");
    });
  });
});
