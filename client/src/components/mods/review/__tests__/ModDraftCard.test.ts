import { flushPromises, mount } from "@vue/test-utils";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ModDraft } from "@/lib/mods/kept";

const { setDraftOnMock } = vi.hoisted(() => ({ setDraftOnMock: vi.fn() }));

vi.mock("@/stores/mods", () => ({
  useModsStore: () => ({ setDraftOn: setDraftOnMock }),
}));

import { ModsRequestError } from "@/lib/mods/kept-api";
import ModDraftCard from "@/components/mods/review/ModDraftCard.vue";

function draft(overrides: Partial<ModDraft> = {}): ModDraft {
  return { sessionId: "s1", name: "test-chips", description: "Test runs as counts", version: "1", off: null, kept: null, ...overrides };
}

function mountCard(value: ModDraft, phone = false) {
  return mount(ModDraftCard, { props: { draft: value, phone } });
}

beforeEach(() => {
  setDraftOnMock.mockReset().mockResolvedValue(undefined);
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

  it("says a draft whose manifest is missing is still being written", () => {
    const wrapper = mountCard(draft({ description: null, version: null }));
    expect(wrapper.text()).toContain("Still being written");
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

  it("shows the draft's problem in the error colour", () => {
    const wrapper = mountCard(draft({ problem: { code: "x", message: "Call on() inside register", line: 12 } }));
    expect(wrapper.find("[data-testid=mod-draft-check]").text()).toBe("Check: Call on() inside register (line 12)");
    const noLine = mountCard(draft({ problem: { code: "x", message: "Bad manifest" } }));
    expect(noLine.find("[data-testid=mod-draft-check]").text()).toBe("Check: Bad manifest");
  });

  it("shows nothing for a null problem", () => {
    expect(mountCard(draft({ problem: null })).find("[data-testid=mod-draft-check]").exists()).toBe(false);
  });

  it("shows the agent's request to keep, with its note", () => {
    const wrapper = mountCard(draft({ keepRequest: { note: "Counts are easier to scan.", at: "2026-10-10T10:00:00Z" } }));
    expect(wrapper.find("[data-testid=mod-draft-request]").text()).toContain("The agent asks you to keep this");
    expect(wrapper.find("[data-testid=mod-draft-request]").text()).toContain("Counts are easier to scan.");
  });

  it("shows the request without a note", () => {
    const wrapper = mountCard(draft({ keepRequest: { note: null, at: "2026-10-10T10:00:00Z" } }));
    const line = wrapper.find("[data-testid=mod-draft-request]").text();
    expect(line).toContain("The agent asks you to keep this");
    expect(line).not.toContain("Review");
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
    it("draws the phone's own buttons, never the desktop ones", () => {
      const wrapper = mountCard(draft(), true);
      expect(wrapper.find("[data-testid=mod-draft-toggle]").classes()).toEqual(expect.arrayContaining(["ph-btn", "ph-btn--outline"]));
      expect(wrapper.find("[data-slot=button]").exists()).toBe(false);
    });
  });
});
