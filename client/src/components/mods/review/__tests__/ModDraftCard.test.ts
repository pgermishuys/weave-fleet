import { flushPromises, mount } from "@vue/test-utils";
import { defineComponent, h, reactive } from "vue";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ModCheckReport, ModDraft } from "@/lib/mods/kept";

const { checkDraftMock, setDraftOnMock, store } = vi.hoisted(() => ({
  checkDraftMock: vi.fn(),
  setDraftOnMock: vi.fn(),
  store: { drafts: {} as Record<string, unknown[]> },
}));

vi.mock("@/lib/mods/kept-api", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/mods/kept-api")>()),
  checkDraft: checkDraftMock,
}));
vi.mock("@/stores/mods", () => ({
  useModsStore: () => ({ drafts: store.drafts, setDraftOn: setDraftOnMock }),
}));
vi.mock("@/stores/machines", () => ({
  useMachinesStore: () => ({ sessionTarget: () => ({ connection: null }) }),
}));
vi.mock("@/components/mods/review/ModReviewDialog.vue", () => ({
  default: defineComponent({ name: "ModReviewDialog", props: ["open", "sessionId", "draft", "phone"], setup: (p) => () => h("div", { "data-testid": "review", "data-open": String(p.open), "data-phone": String(p.phone) }) }),
}));
vi.mock("@/components/mods/review/ModCodeDialog.vue", () => ({
  default: defineComponent({ name: "ModCodeDialog", props: ["open", "title", "load", "phone"], setup: (p) => () => h("div", { "data-testid": "code", "data-open": String(p.open), "data-title": p.title, "data-phone": String(p.phone) }) }),
}));

import { ModsRequestError } from "@/lib/mods/kept-api";
import ModDraftCard from "@/components/mods/review/ModDraftCard.vue";

function draft(overrides: Partial<ModDraft> = {}): ModDraft {
  return { sessionId: "s1", name: "test-chips", description: "Test runs as counts", version: "1", off: null, kept: null, ...overrides };
}

function report(overrides: Partial<ModCheckReport> = {}): ModCheckReport {
  return {
    ok: true, name: "test-chips", version: "1", description: "x", lines: 47, sha256: "a",
    hooks: [], calls: [], state: [], pages: [], errors: [], warnings: [], ...overrides,
  };
}

function mountCard(value: ModDraft, phone = false) {
  return mount(ModDraftCard, { props: { draft: value, phone } });
}

beforeEach(() => {
  checkDraftMock.mockReset().mockResolvedValue(null);
  setDraftOnMock.mockReset().mockResolvedValue(undefined);
  store.drafts = reactive({});
});

describe("ModDraftCard", () => {
  it("shows a draft that is on in this session only", async () => {
    const wrapper = mountCard(draft());
    await flushPromises();
    expect(wrapper.find("[data-testid=mod-draft-mark]").exists()).toBe(true);
    expect(wrapper.text()).toContain("test-chips");
    expect(wrapper.text()).toContain("On in this session only");
    expect(wrapper.text()).toContain("Test runs as counts");
    expect(wrapper.text()).toContain("Turn off");
    expect(wrapper.find("[data-testid=mod-draft-keep]").attributes("disabled")).toBeUndefined();
  });

  it("says when it is off, and turns it on again", async () => {
    const wrapper = mountCard(draft({ off: { by: "user", at: "2026-10-10T10:00:00Z" } }));
    expect(wrapper.text()).toContain("Off in this session");
    await wrapper.find("[data-testid=mod-draft-toggle]").trigger("click");
    expect(wrapper.find("[data-testid=mod-draft-toggle]").text()).toContain("Turn on again");
    expect(setDraftOnMock).toHaveBeenCalledWith("s1", "test-chips", true);
  });

  it("says three failures turned it off, with the last error", () => {
    const wrapper = mountCard(draft({ off: { by: "strikes", at: "2026-10-10T10:00:00Z", error: "x is not a function" } }));
    expect(wrapper.text()).toContain("Turned off after three failures");
    expect(wrapper.text()).toContain("x is not a function");
  });

  it("says a draft whose manifest is missing is still being written, and can't be kept yet", () => {
    const wrapper = mountCard(draft({ description: null, version: null }));
    expect(wrapper.text()).toContain("Still being written");
    expect(wrapper.find("[data-testid=mod-draft-keep]").attributes("disabled")).toBeDefined();
  });

  it("says which kept version it replaces", () => {
    expect(mountCard(draft({ kept: 2 })).text()).toContain("Replaces your kept v2 in this session");
  });

  it("turns off at once, disabled while it waits", async () => {
    let finish: () => void = () => {};
    setDraftOnMock.mockReturnValue(new Promise<void>((resolve) => { finish = resolve; }));
    const wrapper = mountCard(draft());
    await wrapper.find("[data-testid=mod-draft-toggle]").trigger("click");
    expect(setDraftOnMock).toHaveBeenCalledWith("s1", "test-chips", false);
    expect(wrapper.find("[data-testid=mod-draft-toggle]").attributes("disabled")).toBeDefined();
    finish();
    await flushPromises();
    expect(wrapper.find("[data-testid=mod-draft-toggle]").attributes("disabled")).toBeUndefined();
  });

  it("shows why turning off failed", async () => {
    setDraftOnMock.mockRejectedValue(new ModsRequestError("That draft is gone.", 404));
    const wrapper = mountCard(draft());
    await wrapper.find("[data-testid=mod-draft-toggle]").trigger("click");
    await flushPromises();
    expect(wrapper.find("[data-testid=mod-draft-action-error]").text()).toBe("That draft is gone.");
  });

  it("opens the review and the code", async () => {
    const wrapper = mountCard(draft());
    await wrapper.find("[data-testid=mod-draft-keep]").trigger("click");
    expect(wrapper.find("[data-testid=review]").attributes("data-open")).toBe("true");
    await wrapper.find("[data-testid=mod-draft-code]").trigger("click");
    expect(wrapper.find("[data-testid=code]").attributes("data-open")).toBe("true");
    expect(wrapper.find("[data-testid=code]").attributes("data-title")).toBe("test-chips · draft");
  });

  it("never checks on its own", async () => {
    const wrapper = mountCard(draft());
    await flushPromises();
    store.drafts["s1"] = [draft({ version: "2" })];
    await flushPromises();
    expect(checkDraftMock).not.toHaveBeenCalled();
    expect(wrapper.find("[data-testid=mod-draft-check]").exists()).toBe(false);
  });

  it("shows the draft's problem in the error colour", () => {
    const wrapper = mountCard(draft({ problem: { code: "x", message: "Call on() inside register", line: 12 } }));
    expect(wrapper.find("[data-testid=mod-draft-check]").text()).toBe("Check: Call on() inside register (line 12)");
    const noLine = mountCard(draft({ problem: { code: "x", message: "Bad manifest" } }));
    expect(noLine.find("[data-testid=mod-draft-check]").text()).toBe("Check: Bad manifest");
  });

  it("shows nothing for a null problem", () => {
    expect(mountCard(draft({ problem: null })).find("[data-testid=mod-draft-check]").exists()).toBe(false);
  });

  it("checks once on Check again, saying Checking… meanwhile, then shows the first error", async () => {
    let finish: (value: ModCheckReport) => void = () => {};
    checkDraftMock.mockReturnValue(new Promise<ModCheckReport>((resolve) => { finish = resolve; }));
    const wrapper = mountCard(draft({ problem: { code: "old", message: "Old problem" } }));
    await wrapper.find("[data-testid=mod-draft-recheck]").trigger("click");
    expect(wrapper.find("[data-testid=mod-draft-recheck]").text()).toBe("Checking…");
    expect(wrapper.find("[data-testid=mod-draft-recheck]").attributes("disabled")).toBeDefined();
    finish(report({ ok: false, errors: [{ line: 4, column: 1, code: "x", message: "Call on() inside register" }] }));
    await flushPromises();
    expect(checkDraftMock).toHaveBeenCalledTimes(1);
    expect(checkDraftMock).toHaveBeenCalledWith("s1", "test-chips", null);
    expect(wrapper.find("[data-testid=mod-draft-check]").text()).toBe("Check: Call on() inside register (line 4)");
    expect(wrapper.text()).not.toContain("Old problem");
  });

  it("says no problems were found", async () => {
    checkDraftMock.mockResolvedValue(report());
    const wrapper = mountCard(draft({ problem: { code: "old", message: "Old problem" } }));
    await wrapper.find("[data-testid=mod-draft-recheck]").trigger("click");
    await flushPromises();
    expect(wrapper.find("[data-testid=mod-draft-check]").text()).toBe("No problems found");
  });

  it("says when the runtime isn't running (null report) or the check fails", async () => {
    const wrapper = mountCard(draft());
    await wrapper.find("[data-testid=mod-draft-recheck]").trigger("click");
    await flushPromises();
    expect(wrapper.find("[data-testid=mod-draft-check]").text()).toBe("Fleet couldn't check it: the mod runtime isn't running");
    checkDraftMock.mockRejectedValue(new ModsRequestError("That draft is gone.", 404));
    await wrapper.find("[data-testid=mod-draft-recheck]").trigger("click");
    await flushPromises();
    expect(wrapper.find("[data-testid=mod-draft-check]").text()).toBe("That draft is gone.");
  });

  it("goes back to the draft's own problem when the draft changes", async () => {
    checkDraftMock.mockResolvedValue(report());
    const wrapper = mountCard(draft({ problem: { code: "old", message: "Old problem" } }));
    await wrapper.find("[data-testid=mod-draft-recheck]").trigger("click");
    await flushPromises();
    await wrapper.setProps({ draft: draft({ version: "2", problem: { code: "new", message: "New problem" } }) });
    expect(wrapper.find("[data-testid=mod-draft-check]").text()).toBe("Check: New problem");
  });

  it("shows the agent's request to keep, with its note, and Review… opens the dialog", async () => {
    const wrapper = mountCard(draft({ keepRequest: { note: "Counts are easier to scan.", at: "2026-10-10T10:00:00Z" } }));
    expect(wrapper.find("[data-testid=review]").attributes("data-open")).toBe("false");
    expect(wrapper.find("[data-testid=mod-draft-request]").text()).toContain("The agent asks you to keep this");
    expect(wrapper.find("[data-testid=mod-draft-request]").text()).toContain("Counts are easier to scan.");
    await wrapper.find("[data-testid=mod-draft-review]").trigger("click");
    expect(wrapper.find("[data-testid=review]").attributes("data-open")).toBe("true");
  });

  it("shows the request without a note", () => {
    const wrapper = mountCard(draft({ keepRequest: { note: null, at: "2026-10-10T10:00:00Z" } }));
    const line = wrapper.find("[data-testid=mod-draft-request]").text();
    expect(line).toContain("The agent asks you to keep this");
    expect(line).toContain("Review…");
  });

  it("has no request line without a request, and stops showing it when the request goes", async () => {
    const wrapper = mountCard(draft({ keepRequest: { note: null, at: "2026-10-10T10:00:00Z" } }));
    await wrapper.setProps({ draft: draft({ keepRequest: null }) });
    expect(wrapper.find("[data-testid=mod-draft-request]").exists()).toBe(false);
    expect(mountCard(draft()).find("[data-testid=mod-draft-request]").exists()).toBe(false);
  });

  it("shows no line number for a null line", () => {
    const wrapper = mountCard(draft({ problem: { code: "x", message: "Bad manifest", line: null } }));
    expect(wrapper.find("[data-testid=mod-draft-check]").text()).toBe("Check: Bad manifest");
  });

  describe("on the phone", () => {
    it("draws the phone's own buttons in a row, never the desktop ones", () => {
      const wrapper = mountCard(draft(), true);
      const keep = wrapper.find("[data-testid=mod-draft-keep]");
      expect(keep.classes()).toEqual(expect.arrayContaining(["ph-btn", "ph-btn--sm", "ph-btn--primary"]));
      expect(wrapper.find("[data-testid=mod-draft-code]").classes()).toEqual(expect.arrayContaining(["ph-btn", "ph-btn--outline"]));
      expect(wrapper.find("[data-testid=mod-draft-toggle]").classes()).toEqual(expect.arrayContaining(["ph-btn", "ph-btn--outline"]));
      expect(wrapper.find("[data-slot=button]").exists()).toBe(false);
    });

    it("passes phone on to the dialogs", () => {
      const wrapper = mountCard(draft(), true);
      expect(wrapper.find("[data-testid=review]").attributes("data-phone")).toBe("true");
      expect(wrapper.find("[data-testid=code]").attributes("data-phone")).toBe("true");
    });
  });
});
