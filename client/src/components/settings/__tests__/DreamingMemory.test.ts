import { enableAutoUnmount, flushPromises, mount } from "@vue/test-utils";
import { computed, ref } from "vue";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { DreamChangeView, DreamView, NightlyDreamingView } from "@/lib/dreaming";

const mocks = vi.hoisted(() => ({
  on: { value: false } as { value: boolean },
  getMemory: vi.fn(),
  listMemoryNotes: vi.fn(),
  listDreams: vi.fn(),
  startDream: vi.fn(),
  undoDreamChange: vi.fn(),
  setNightlyDreaming: vi.fn(),
  getNightlyDreaming: vi.fn(),
}));

vi.mock("@/lib/agent-memory", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/agent-memory")>()),
  getMemory: mocks.getMemory,
  listMemoryNotes: mocks.listMemoryNotes,
}));
vi.mock("@/lib/dreaming", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/dreaming")>()),
  listDreams: mocks.listDreams,
  startDream: mocks.startDream,
  undoDreamChange: mocks.undoDreamChange,
  getNightlyDreaming: mocks.getNightlyDreaming,
  setNightlyDreaming: mocks.setNightlyDreaming,
}));
vi.mock("@/composables/use-dreaming-feature", () => ({
  useDreamingFeature: () => ({ isDreamingEnabled: computed(() => mocks.on.value) }),
}));
vi.mock("@/composables/use-enabled-harnesses", () => ({
  useEnabledHarnesses: () => ({
    enabledHarnesses: ref([
      { type: "opencode", displayName: "OpenCode" },
      { type: "opencode2", displayName: "OpenCode 2" },
    ]),
  }),
}));
vi.mock("@/composables/use-harness-catalog", () => ({ useHarnessCatalog: () => ({ catalog: ref(null), models: ref([]) }) }));
vi.mock("@tanstack/vue-router", () => ({ useNavigate: () => vi.fn() }));

const { default: MemorySection } = await import("@/components/settings/MemorySection.vue");

enableAutoUnmount(afterEach);

const session = (id: string, title: string) => ({ id, title, harnessType: "opencode2" });

function change(kind: DreamChangeView["kind"], overrides: Partial<DreamChangeView> = {}): DreamChangeView {
  return {
    id: `c-${kind}`,
    kind,
    list: "repository",
    noteId: "n1",
    text: `${kind} note text`,
    replaced: [],
    why: `Why ${kind}`,
    sessions: [session("s1", "Fix the sprocket"), session("s2", "Tune the gizmo")],
    ofSessions: 14,
    quote: "use the blue cable",
    reason: null,
    undone: false,
    ...overrides,
  };
}

function dreamView(overrides: Partial<DreamView> = {}): DreamView {
  return {
    id: "d1",
    repository: "/src/acme-widgets",
    repositoryName: "acme-widgets",
    sessionId: "dream-session",
    harnessType: "opencode2",
    status: "done",
    error: null,
    started: "2026-10-27T01:00:00Z",
    finished: "2026-10-27T01:05:00Z",
    readSessions: [session("s1", "Fix the sprocket"), session("s2", "Tune the gizmo")],
    runId: null,
    tokens: null,
    since: null,
    changes: [
      change("added"),
      change("rewritten", { replaced: [{ id: "o1", text: "old sprocket advice", kind: "learned", list: "repository" }] }),
      change("merged", { replaced: [{ id: "o2", text: "removed duplicate note", kind: "learned", list: "repository" }] }),
      change("left", { reason: "Only one session mentioned it", sessions: [] }),
      change("skipped", { id: "c-skip", text: "seen once" }),
    ],
    ...overrides,
  };
}

function nightlyView(overrides: Partial<NightlyDreamingView> = {}): NightlyDreamingView {
  return { enabled: false, automationId: null, paused: false, time: "03:00", timeZone: null, harnessType: null, model: null, budgetTokens: 300000, nextRunAt: null, recent: [], ...overrides };
}

async function mountSection() {
  const wrapper = mount(MemorySection);
  await flushPromises();
  return wrapper;
}

describe("Dreaming in Settings → Memory", () => {
  beforeEach(() => {
    localStorage.clear();
    mocks.on.value = true;
    Object.values(mocks).forEach((mock) => typeof mock === "function" && mock.mockReset());
    mocks.getMemory.mockResolvedValue({
      enabled: true,
      repositories: [{ path: "/src/acme-widgets", name: "acme-widgets", count: 1 }],
      machineCount: 0,
      maxRepositoryNotes: 40,
      maxMachineNotes: 15,
    });
    mocks.listMemoryNotes.mockResolvedValue({ repository: "/src/acme-widgets", repositoryNotes: [], machineNotes: [], tokens: 0 });
    mocks.listDreams.mockResolvedValue([dreamView()]);
    mocks.getNightlyDreaming.mockResolvedValue(nightlyView());
  });

  it("renders nothing and asks for no dreams while the switch is off", async () => {
    mocks.on.value = false;
    const wrapper = await mountSection();

    expect(wrapper.find("[data-testid='dreaming-panel']").exists()).toBe(false);
    expect(mocks.listDreams).not.toHaveBeenCalled();
  });

  it("shows the latest dream's cards, evidence and the skipped group", async () => {
    const wrapper = await mountSection();
    const text = wrapper.get("[data-testid='dreaming-panel']").text();

    expect(text).toContain("Last dream · acme-widgets · 3 notes changed");
    expect(text).toContain("Read 2 sessions");
    for (const label of ["Added", "Rewritten", "Merged", "Left as is"]) expect(text).toContain(label);
    expect(text).toContain("old sprocket advice");
    expect(text).toContain("Only one session mentioned it");
    expect(text).toContain("Seen in one session only, not changed (1)");

    expect(wrapper.find("[data-testid='dream-evidence']").exists()).toBe(false);
    await wrapper.findAll("[data-testid='dream-why']")[0]!.trigger("click");
    const evidence = wrapper.get("[data-testid='dream-evidence']").text();
    expect(evidence).toContain("2 of 14 sessions");
    expect(evidence).toContain("Fix the sprocket · OpenCode 2");
    expect(evidence).toContain("use the blue cable");
  });

  it("undoes a change, shows Undone and reads the notes again", async () => {
    mocks.undoDreamChange.mockResolvedValue(dreamView({ changes: [change("added", { undone: true })] }));
    const wrapper = await mountSection();
    const notesReads = mocks.listMemoryNotes.mock.calls.length;

    await wrapper.findAll("[data-testid='dream-undo']")[0]!.trigger("click");
    await flushPromises();

    expect(mocks.undoDreamChange).toHaveBeenCalledWith("d1", "c-added");
    const undo = wrapper.get("[data-testid='dream-undo']");
    expect(undo.text()).toBe("Undone");
    expect(undo.attributes("disabled")).toBeDefined();
    expect(mocks.listMemoryNotes.mock.calls.length).toBeGreaterThan(notesReads);
  });

  it("shows no nightly row and asks nothing while the switch is off", async () => {
    mocks.on.value = false;
    const wrapper = await mountSection();

    expect(wrapper.find("[data-testid='dream-nightly']").exists()).toBe(false);
    expect(mocks.getNightlyDreaming).not.toHaveBeenCalled();
  });

  it("turns nightly dreaming on with the time zone, then saves the time and the budget", async () => {
    mocks.setNightlyDreaming.mockImplementation(async (change) => nightlyView({ enabled: true, ...change }));
    const wrapper = await mountSection();

    expect(wrapper.find("[data-testid='dream-time']").exists()).toBe(false);
    await wrapper.get("[data-testid='dream-nightly-switch']").trigger("click");
    await flushPromises();
    expect(mocks.setNightlyDreaming).toHaveBeenLastCalledWith({ enabled: true, timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone });
    expect(wrapper.get("[data-testid='dream-nightly']").text()).toContain("Up to 300k tokens a night");

    await wrapper.get("[data-testid='dream-time']").setValue("04:30");
    await flushPromises();
    expect(mocks.setNightlyDreaming).toHaveBeenLastCalledWith({ time: "04:30", timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone });

    await wrapper.get("[data-testid='dream-budget']").setValue("1000000");
    await flushPromises();
    expect(mocks.setNightlyDreaming).toHaveBeenLastCalledWith({ budgetTokens: 1000000 });
  });

  it("says when the automation is paused, and links to it", async () => {
    mocks.getNightlyDreaming.mockResolvedValue(nightlyView({ enabled: true, paused: true, automationId: "a1" }));
    const wrapper = await mountSection();

    expect(wrapper.get("[data-testid='dream-paused']").text()).toBe("Paused in Automations");
    expect(wrapper.get("[data-testid='dream-automation']").text()).toBe("Runs as the built-in automation Dreaming");
  });

  it("names a night's dream and its tokens, and shows a kept note with Undo", async () => {
    mocks.listDreams.mockResolvedValue([dreamView({ runId: "r1", tokens: 186000, changes: [change("kept")] })]);
    const wrapper = await mountSection();
    const text = wrapper.get("[data-testid='dreaming-panel']").text();

    expect(text).toContain("Last night · acme-widgets · 1 note changed");
    expect(text).toContain("186k tokens");
    expect(text).toContain("Kept for good");
    expect(wrapper.get("[data-testid='dream-undo']").text()).toBe("Undo");
  });

  it("starts a dream on the chosen harness and shows it running", async () => {
    mocks.listDreams.mockResolvedValue([]);
    mocks.startDream.mockResolvedValue(dreamView({ status: "reading", sessionId: null, changes: [], finished: null }));
    const wrapper = await mountSection();

    expect((wrapper.get("[data-testid='dreaming-harness']").element as HTMLSelectElement).value).toBe("opencode2");
    await wrapper.get("[data-testid='dreaming-harness']").setValue("opencode");
    await wrapper.get("[data-testid='dream-now']").trigger("click");
    await flushPromises();

    expect(mocks.startDream).toHaveBeenCalledWith("/src/acme-widgets", { harnessType: "opencode" });
    expect(wrapper.get("[data-testid='dream-running']").text()).toContain("reading 2 sessions");
  });
});
