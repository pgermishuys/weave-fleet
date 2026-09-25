import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { readStoredDraft, storedDraftKeys, writeStoredDraft } from "@/lib/draft-storage";
import { clearDraftText, useDraftState } from "@/composables/use-draft-state";

describe("draft storage", () => {
  beforeEach(() => localStorage.clear());
  afterEach(() => vi.restoreAllMocks());

  it("keeps a draft and forgets an empty one", () => {
    writeStoredDraft("session.a", "half a thought");
    expect(readStoredDraft("session.a")).toBe("half a thought");

    writeStoredDraft("session.a", "");
    expect(readStoredDraft("session.a")).toBeNull();
    expect(localStorage.length).toBe(0);
  });

  it("lists keys by their start", () => {
    writeStoredDraft("side.s1.x", "one");
    writeStoredDraft("side.s1.y", "two");
    writeStoredDraft("side.s2.z", "three");

    expect(storedDraftKeys("side.s1.").sort()).toEqual(["side.s1.x", "side.s1.y"]);
  });

  it("works in memory when storage throws", () => {
    vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => { throw new Error("blocked"); });
    vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => { throw new Error("full"); });
    vi.spyOn(Storage.prototype, "key").mockImplementation(() => { throw new Error("blocked"); });

    expect(() => writeStoredDraft("session.a", "text")).not.toThrow();
    expect(readStoredDraft("session.a")).toBeNull();
    expect(storedDraftKeys("session.")).toEqual([]);

    const { draft, setText } = useDraftState("draft-storage-blocked", { agentId: "", modelId: "" });
    setText("still here");
    expect(draft.text).toBe("still here");
  });
});

describe("composer drafts", () => {
  beforeEach(() => localStorage.clear());

  it("are kept in the browser, and cleared on send", () => {
    const { setText, resetText } = useDraftState("draft-storage-kept", { agentId: "", modelId: "" });

    setText("what I was typing");
    expect(readStoredDraft("session.draft-storage-kept")).toBe("what I was typing");

    resetText();
    expect(readStoredDraft("session.draft-storage-kept")).toBeNull();

    setText("again");
    clearDraftText("draft-storage-kept");
    expect(readStoredDraft("session.draft-storage-kept")).toBeNull();
  });

  it("come back after a reload", () => {
    writeStoredDraft("session.draft-storage-reloaded", "before the reload");

    const { draft } = useDraftState("draft-storage-reloaded", { agentId: "", modelId: "" });

    expect(draft.text).toBe("before the reload");
  });
});
