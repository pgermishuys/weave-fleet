import { enableAutoUnmount, flushPromises, mount } from "@vue/test-utils";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { MemoryNote, MemoryNotes, MemoryOverview } from "@/lib/agent-memory";

const api = vi.hoisted(() => ({
  getMemory: vi.fn(),
  setMemoryEnabled: vi.fn(),
  listMemoryNotes: vi.fn(),
  addMemoryNote: vi.fn(),
  updateMemoryNote: vi.fn(),
  forgetMemoryNote: vi.fn(),
  keepMemoryNote: vi.fn(),
  clearMemory: vi.fn(),
  moveMemoryNote: vi.fn(),
}));

vi.mock("@/lib/agent-memory", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/agent-memory")>()),
  ...api,
}));

const { default: MemorySection } = await import("@/components/settings/MemorySection.vue");

enableAutoUnmount(afterEach);

function overview(enabled: boolean): MemoryOverview {
  return {
    enabled,
    repositories: [{ path: "/src/weave-fleet", name: "weave-fleet", count: 1 }],
    machineCount: 2,
    maxRepositoryNotes: 40,
    maxMachineNotes: 15,
  };
}

function note(id: string, text: string, overrides: Partial<MemoryNote> = {}): MemoryNote {
  return {
    id,
    list: "machine",
    text,
    kind: "learned",
    repository: null,
    sessionId: "session-1",
    sessionTitle: "Latency budget",
    created: "2026-09-21T08:00:00Z",
    updated: "2026-09-21T08:00:00Z",
    ...overrides,
  };
}

const notes: MemoryNotes = {
  repository: "/src/weave-fleet",
  repositoryNotes: [note("aaaa0001", "Rebase onto origin/main; don't merge main.", { list: "repository", repository: "/src/weave-fleet", kind: "from-you" })],
  machineNotes: [note("bbbb0002", "/tmp fills up here; use ~/.cache."), note("cccc0003", "7 GB of memory: one dotnet app at a time.", { kind: "added", sessionTitle: null })],
  tokens: 640,
};

async function mountSection() {
  const wrapper = mount(MemorySection);
  await flushPromises();
  return wrapper;
}

beforeEach(() => {
  vi.clearAllMocks();
  api.getMemory.mockResolvedValue(overview(true));
  api.listMemoryNotes.mockResolvedValue(notes);
});

describe("MemorySection", () => {
  it("is off until the user turns it on, and says what turning it on costs and who reads the notes", async () => {
    api.getMemory.mockResolvedValue(overview(false));
    api.setMemoryEnabled.mockResolvedValue(overview(true));
    const wrapper = await mountSection();

    expect(wrapper.get("[data-testid=memory-switch]").attributes("aria-checked")).toBe("false");
    expect(wrapper.get("[data-testid=memory-state]").text()).toContain("Off.");
    expect(wrapper.text()).toContain("Costs tokens.");
    expect(wrapper.get("[data-testid=memory-claude-warning]").text()).toContain("Claude Code also keeps its own memory");
    expect(wrapper.find("[data-testid=memory-list-machine]").exists()).toBe(false);

    await wrapper.get("[data-testid=memory-switch]").trigger("click");
    await flushPromises();

    expect(api.setMemoryEnabled).toHaveBeenCalledWith(true);
    expect(wrapper.get("[data-testid=memory-switch]").attributes("aria-checked")).toBe("true");
  });

  it("lists the repository's notes and the machine's, with where each came from", async () => {
    const wrapper = await mountSection();

    expect(api.listMemoryNotes).toHaveBeenCalledWith("/src/weave-fleet");
    expect(wrapper.get("[data-testid=memory-count]").text()).toBe("3 notes · about 640 tokens per request");
    const repository = wrapper.get("[data-testid=memory-list-repository]").text();
    expect(repository).toContain("Rebase onto origin/main");
    expect(repository).toContain("From you");
    const machine = wrapper.get("[data-testid=memory-list-machine]").text();
    expect(machine).toContain("/tmp fills up here");
    expect(machine).toContain("“Latency budget”");
    expect(machine).toContain("Added by you");
  });

  it("filters the notes by what the user searches for", async () => {
    const wrapper = await mountSection();

    await wrapper.get("input[type=search]").setValue("dotnet");

    expect(wrapper.get("[data-testid=memory-list-machine]").findAll("li")).toHaveLength(1);
    expect(wrapper.find("[data-testid=memory-list-repository]").exists()).toBe(false);
  });

  it("adds a note the user writes to the list they picked", async () => {
    api.addMemoryNote.mockResolvedValue(note("dddd0004", "Use gh for GitHub."));
    const wrapper = await mountSection();

    await wrapper.get("[data-testid=memory-add-machine]").trigger("click");
    await wrapper.get("[data-testid=memory-draft-machine]").setValue("Use gh for GitHub.");
    await wrapper.get("[data-testid=memory-draft-machine]").trigger("keydown", { key: "Enter" });
    await flushPromises();

    expect(api.addMemoryNote).toHaveBeenCalledWith("machine", null, "Use gh for GitHub.");
  });

  it("forgets a note", async () => {
    api.forgetMemoryNote.mockResolvedValue(undefined);
    const wrapper = await mountSection();

    await wrapper.get("[aria-label='Forget note: /tmp fills up here; use ~/.cache.']").trigger("click");
    await flushPromises();

    expect(api.forgetMemoryNote).toHaveBeenCalledWith("bbbb0002");
  });

  it("asks before clearing all memory", async () => {
    api.clearMemory.mockResolvedValue({ deleted: 3 });
    const wrapper = await mountSection();

    await wrapper.get("[data-testid=memory-clear-all]").trigger("click");
    expect(api.clearMemory).not.toHaveBeenCalled();
    expect(wrapper.text()).toContain("This can't be undone.");

    await wrapper.get("[data-testid=memory-clear-all-confirm]").trigger("click");
    await flushPromises();

    expect(api.clearMemory).toHaveBeenCalledWith("all");
  });

  it("says how long a learned note has left and keeps it for good on request", async () => {
    api.listMemoryNotes.mockResolvedValue({
      ...notes,
      machineNotes: [note("bbbb0002", "/tmp fills up here; use ~/.cache.", { lifetime: 14, daysLeft: 3, relearned: 1 })],
    });
    api.keepMemoryNote.mockResolvedValue(note("bbbb0002", "/tmp fills up here; use ~/.cache.", { lifetime: null }));
    const wrapper = await mountSection();

    expect(wrapper.get("[data-testid=memory-life-bbbb0002]").text()).toBe("3 more days of use · learned 2×");
    expect(wrapper.find("[data-testid=memory-life-aaaa0001]").exists()).toBe(false);

    await wrapper.get("[data-testid=memory-keep-bbbb0002]").trigger("click");
    await flushPromises();

    expect(api.keepMemoryNote).toHaveBeenCalledWith("bbbb0002");
  });

  it("says how a learned note is known: tested with how, or untested; notes from you say nothing", async () => {
    api.listMemoryNotes.mockResolvedValue({
      ...notes,
      machineNotes: [
        note("bbbb0002", "Use gh for GitHub.", { how: "ran gh pr list" }),
        note("cccc0003", "Disk fills at /tmp."),
        note("dddd0004", "Prefer short answers.", { kind: "from-you" }),
      ],
    });
    const wrapper = await mountSection();

    expect(wrapper.get("[data-testid=memory-how-bbbb0002]").text()).toBe("Tested · ran gh pr list");
    expect(wrapper.get("[data-testid=memory-how-cccc0003]").text()).toBe("Untested");
    expect(wrapper.find("[data-testid=memory-how-dddd0004]").exists()).toBe(false);
  });

  it("says a learned note was kept, and offers no Keep for it", async () => {
    api.listMemoryNotes.mockResolvedValue({
      ...notes,
      machineNotes: [note("bbbb0002", "/tmp fills up here; use ~/.cache.", { lifetime: null, relearned: 1 })],
    });
    const wrapper = await mountSection();

    expect(wrapper.get("[data-testid=memory-life-bbbb0002]").text()).toBe("Kept for good · learned 2×");
    expect(wrapper.find("[data-testid=memory-keep-bbbb0002]").exists()).toBe(false);
  });

  it("lists expired notes apart, folded, and brings one back with Keep", async () => {
    api.listMemoryNotes.mockResolvedValue({
      ...notes,
      expiredNotes: [note("eeee0005", "gh isn't on PATH; read GitHub with WebFetch.", { lifetime: 7, daysLeft: 0, expired: true })],
    });
    api.keepMemoryNote.mockResolvedValue(note("eeee0005", "gh isn't on PATH; read GitHub with WebFetch.", { lifetime: null }));
    const wrapper = await mountSection();

    expect(wrapper.get("[data-testid=memory-count]").text()).toBe("3 notes · about 640 tokens per request");
    expect(wrapper.get("[data-testid=memory-expired-toggle]").text()).toContain("Expired");
    expect(wrapper.text()).not.toContain("gh isn't on PATH");

    await wrapper.get("[data-testid=memory-expired-toggle]").trigger("click");
    expect(wrapper.get("[data-testid=memory-expired]").text()).toContain("Expired after 7 days of use");

    await wrapper.get("[data-testid=memory-keep-eeee0005]").trigger("click");
    await flushPromises();

    expect(api.keepMemoryNote).toHaveBeenCalledWith("eeee0005");
  });

  describe("with Search older notes on", () => {
    const repo = (id: string, text: string, overrides: Partial<MemoryNote> = {}) =>
      note(id, text, { list: "repository", repository: "/src/weave-fleet", ...overrides });

    const grouped: MemoryNotes = {
      repository: "/src/weave-fleet",
      repositoryNotes: [
        repo("aaaa0001", "Every PR includes a screenshot.", { kind: "from-you", pinned: true }),
        repo("aaaa0002", "Rebase onto origin/main; don't merge main.", { kind: "from-you", found: 14 }),
        repo("ssss0003", "Run OpenCode 2 live tests under a scratch HOME.", { inSearch: true, found: 2, lastFound: "2026-10-24T08:00:00Z" }),
      ],
      machineNotes: [note("bbbb0002", "/tmp fills up here; use ~/.cache.")],
      tokens: 120,
    };

    beforeEach(() => {
      api.getMemory.mockResolvedValue({
        ...overview(true),
        searchEnabled: true,
        maxAlwaysRepositoryNotes: 15,
        maxAlwaysMachineNotes: 8,
        maxSearchRepositoryNotes: 200,
        maxSearchMachineNotes: 60,
      });
      api.listMemoryNotes.mockResolvedValue(grouped);
    });

    it("shows each list as the notes every session reads and the notes found by search, with counts", async () => {
      const wrapper = await mountSection();

      expect(wrapper.get("[data-testid=memory-group-repository-always]").text()).toMatch(/Every session reads\s*2\/15 · about \d+ tokens/);
      expect(wrapper.get("[data-testid=memory-group-repository-search]").text()).toContain("Found by search");
      expect(wrapper.get("[data-testid=memory-group-repository-search]").text()).toContain("1/200 · no tokens until found");
      expect(wrapper.get("[data-testid=memory-group-machine-always]").text()).toContain("1/8");
      expect(wrapper.get("[data-testid=memory-list-repository]").text()).toContain("Rebase onto origin/main");
      expect(wrapper.get("[data-testid=memory-list-repository]").text()).not.toContain("scratch HOME");
      expect(wrapper.get("[data-testid=memory-list-repository-search]").text()).toContain("scratch HOME");
      expect(wrapper.get("[data-testid=memory-found-ssss0003]").text()).toMatch(/found 2 times · last/);
      expect(wrapper.get("[data-testid=memory-found-aaaa0002]").text()).toBe("found 14 times");
      expect(wrapper.get("[data-testid=memory-pinned-aaaa0001]").text()).toContain("Pinned");
    });

    it("moves a note to search, and pins a searched one to always read", async () => {
      api.moveMemoryNote.mockResolvedValue(note("x", "x"));
      const wrapper = await mountSection();

      await wrapper.get("[data-testid=memory-to-search-aaaa0002]").trigger("click");
      await flushPromises();
      expect(api.moveMemoryNote).toHaveBeenLastCalledWith("aaaa0002", "search");

      await wrapper.get("[data-testid=memory-to-always-ssss0003]").trigger("click");
      await flushPromises();
      expect(api.moveMemoryNote).toHaveBeenLastCalledWith("ssss0003", "always");

      await wrapper.get("[data-testid=memory-pin-aaaa0002]").trigger("click");
      await flushPromises();
      expect(api.moveMemoryNote).toHaveBeenLastCalledWith("aaaa0002", "always");
      expect(wrapper.find("[data-testid=memory-pin-aaaa0001]").exists()).toBe(false);
      expect(wrapper.find("[data-testid=memory-to-always-aaaa0002]").exists()).toBe(false);
    });

    it("keeps filtering both groups with Search notes", async () => {
      const wrapper = await mountSection();

      await wrapper.get("input[type=search]").setValue("scratch");

      expect(wrapper.get("[data-testid=memory-list-repository-search]").text()).toContain("scratch HOME");
      expect(wrapper.find("[data-testid=memory-list-repository]").exists()).toBe(false);
    });

    it("looks the same as today when the switch is off", async () => {
      api.getMemory.mockResolvedValue(overview(true));
      api.listMemoryNotes.mockResolvedValue(grouped);
      const wrapper = await mountSection();

      expect(wrapper.find("[data-testid=memory-group-repository-always]").exists()).toBe(false);
      expect(wrapper.find("[data-testid=memory-to-search-aaaa0002]").exists()).toBe(false);
      expect(wrapper.get("[data-testid=memory-list-repository]").text()).toContain("scratch HOME");
    });
  });
});
