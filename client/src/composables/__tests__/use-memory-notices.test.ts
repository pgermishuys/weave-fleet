import { enableAutoUnmount, flushPromises, mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { defineComponent } from "vue";
import type { MemoryNote, MemorySavedPayload } from "@/lib/agent-memory";

const { handlers, forgetMock, updateMock, addMock } = vi.hoisted(() => ({
  handlers: [] as ((event: { type: string; payload: unknown }) => void)[],
  forgetMock: vi.fn(),
  updateMock: vi.fn(),
  addMock: vi.fn(),
}));

vi.mock("@/composables/use-signalr-socket", () => ({
  onGlobalEvent: (_machine: unknown, topic: string, handler: (event: { type: string; payload: unknown }) => void) => {
    if (topic === "sessions") handlers.push(handler);
    return () => handlers.splice(handlers.indexOf(handler), 1);
  },
}));

vi.mock("@/lib/agent-memory", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/agent-memory")>()),
  forgetMemoryNote: forgetMock,
  updateMemoryNote: updateMock,
  addMemoryNote: addMock,
}));

const { useMemoryNotices } = await import("@/composables/use-memory-notices");
const { useNoticesStore } = await import("@/stores/notices");

enableAutoUnmount(afterEach);

const Host = defineComponent({
  setup() {
    useMemoryNotices();
    return () => null;
  },
});

function note(overrides: Partial<MemoryNote> = {}): MemoryNote {
  return {
    id: "1a2b3c4d",
    list: "machine",
    text: "WebFetch can't read github.com pages; use gh.",
    kind: "learned",
    repository: null,
    sessionId: "session-1",
    sessionTitle: "Summarize PR #328 review",
    created: "2026-09-27T08:00:00Z",
    updated: "2026-09-27T08:00:00Z",
    ...overrides,
  };
}

function save(payload: MemorySavedPayload) {
  for (const handler of handlers) handler({ type: "memory.saved", payload });
}

async function undo() {
  const store = useNoticesStore();
  const action = store.notices[0]?.actions?.find((item) => item.label === "Undo");
  await action?.run();
  await flushPromises();
}

beforeEach(() => {
  setActivePinia(createPinia());
  vi.clearAllMocks();
  handlers.length = 0;
  mount(Host);
});

describe("useMemoryNotices", () => {
  it("shows a saved note with Undo for six seconds, with the time left as a bar", () => {
    save({ note: note(), repositoryName: null, previous: null });

    const [shown] = useNoticesStore().notices;
    expect(shown).toMatchObject({
      title: "Remembered for this machine",
      body: "WebFetch can't read github.com pages; use gh.",
      holdMs: 6000,
      countdown: true,
    });
    expect(shown?.chip).toBeUndefined();
    expect(shown?.actions?.map((action) => action.label)).toEqual(["Undo"]);
  });

  it("names the repository a repository note is for", () => {
    save({ note: note({ list: "repository", repository: "/src/weave-fleet" }), repositoryName: "weave-fleet", previous: null });

    expect(useNoticesStore().notices[0]?.title).toBe("Remembered for weave-fleet");
  });

  it("Undo forgets a new note and closes the notice", async () => {
    save({ note: note(), repositoryName: null, previous: null });

    await undo();

    expect(forgetMock).toHaveBeenCalledWith("1a2b3c4d");
    expect(useNoticesStore().notices).toHaveLength(0);
  });

  it("Undo puts back the note a correction replaced", async () => {
    const previous = note({ text: "Client tests run with bun run test:unit." });
    save({ note: note({ text: "Client tests run with bun run test." }), repositoryName: null, previous });

    await undo();

    expect(updateMock).toHaveBeenCalledWith("1a2b3c4d", "Client tests run with bun run test:unit.");
    expect(forgetMock).not.toHaveBeenCalled();
  });

  it("says so when Undo fails, and keeps the notice", async () => {
    forgetMock.mockRejectedValueOnce(new Error("Fleet isn't reachable."));
    save({ note: note(), repositoryName: null, previous: null });

    await undo();

    expect(useNoticesStore().notices[0]).toMatchObject({ title: "Couldn't undo it", body: "Fleet isn't reachable.", actions: [] });
  });

  it("ignores other events on the sessions topic", () => {
    for (const handler of handlers) handler({ type: "smart_link.updated", payload: { id: "x" } });

    expect(useNoticesStore().notices).toHaveLength(0);
  });
});
