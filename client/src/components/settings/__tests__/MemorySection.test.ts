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
  clearMemory: vi.fn(),
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
});
