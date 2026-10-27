import { enableAutoUnmount, flushPromises, mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { computed } from "vue";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { defineComponent } from "vue";
import type { DreamView, NightlyDreamingView } from "@/lib/dreaming";

const mocks = vi.hoisted(() => ({
  on: { value: true } as { value: boolean },
  getNightlyDreaming: vi.fn(),
  navigate: vi.fn(),
  handlers: [] as ((event: { type: string; payload: unknown }) => void)[],
}));

vi.mock("@/composables/use-signalr-socket", () => ({
  onGlobalEvent: (_machine: unknown, topic: string, handler: (event: { type: string; payload: unknown }) => void) => {
    if (topic === "sessions") mocks.handlers.push(handler);
    return () => mocks.handlers.splice(mocks.handlers.indexOf(handler), 1);
  },
}));
vi.mock("@/composables/use-dreaming-feature", () => ({ useDreamingFeature: () => ({ isDreamingEnabled: computed(() => mocks.on.value) }) }));
vi.mock("@tanstack/vue-router", () => ({ useNavigate: () => mocks.navigate }));
vi.mock("@/lib/dreaming", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/dreaming")>()),
  getNightlyDreaming: mocks.getNightlyDreaming,
}));

const { useDreamNotices } = await import("@/composables/use-dream-notices");
const { useNoticesStore } = await import("@/stores/notices");
const { useSettingsNav } = await import("@/composables/use-settings-nav");

enableAutoUnmount(afterEach);

const Host = defineComponent({
  setup() {
    useDreamNotices();
    return () => null;
  },
});

const sessions = (count: number) => Array.from({ length: count }, (_, i) => ({ id: `s${i}`, title: `Session ${i}`, harnessType: null }));
const change = (kind: string, undone = false) => ({ id: `c-${kind}`, kind, undone }) as never;

function dream(overrides: Partial<DreamView> = {}): DreamView {
  return {
    id: "d1",
    repository: "/src/acme-widgets",
    repositoryName: "acme-widgets",
    status: "done",
    runId: "r1",
    readSessions: sessions(14),
    changes: [change("added"), change("rewritten"), change("merged"), change("kept"), change("left")],
    ...overrides,
  } as DreamView;
}

const nightly = (recent: DreamView[]) => ({ enabled: true, recent }) as NightlyDreamingView;

describe("useDreamNotices", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    localStorage.clear();
    mocks.on.value = true;
    mocks.handlers.length = 0;
    mocks.getNightlyDreaming.mockReset();
    mocks.navigate.mockReset();
  });

  it("posts one notice for a night's dream that changed notes", async () => {
    mocks.getNightlyDreaming.mockResolvedValue(nightly([dream()]));
    mount(Host);
    await flushPromises();

    const notice = useNoticesStore().notices.find((item) => item.id === "dream-d1");
    expect(notice?.title).toBe("Fleet updated acme-widgets's notes");
    expect(notice?.body).toBe("14 sessions read overnight · 1 added, 1 rewritten, 1 merged, 1 kept for good");
  });

  it("says nothing for a dream with no applied changes", async () => {
    mocks.getNightlyDreaming.mockResolvedValue(nightly([dream({ changes: [change("left"), change("added", true)] })]));
    mount(Host);
    await flushPromises();

    expect(useNoticesStore().notices).toHaveLength(0);
  });

  it("asks for nothing and shows nothing while the switch is off", async () => {
    mocks.on.value = false;
    mount(Host);
    await flushPromises();

    expect(mocks.getNightlyDreaming).not.toHaveBeenCalled();
    expect(mocks.handlers).toHaveLength(0);
  });

  it("posts when a night's dream ends live, but not for Dream now", async () => {
    mocks.getNightlyDreaming.mockResolvedValue(nightly([]));
    mount(Host);
    await flushPromises();

    for (const handler of mocks.handlers) handler({ type: "memory.dreamed", payload: dream({ id: "manual", runId: null }) });
    expect(useNoticesStore().notices).toHaveLength(0);
    for (const handler of mocks.handlers) handler({ type: "memory.dreamed", payload: dream({ id: "d2" }) });
    expect(useNoticesStore().has("dream-d2")).toBe(true);
  });

  it("opens Settings → Memory on the repository from See what changed", async () => {
    mocks.getNightlyDreaming.mockResolvedValue(nightly([dream()]));
    mount(Host);
    await flushPromises();

    const action = useNoticesStore().notices[0]!.actions![0]!;
    expect(action.label).toBe("See what changed");
    await action.run();

    const { activeSection, memoryRepository } = useSettingsNav();
    expect(activeSection.value).toBe("memory");
    expect(memoryRepository.value).toBe("/src/acme-widgets");
    expect(mocks.navigate).toHaveBeenCalledWith({ to: "/settings" });
  });
});
