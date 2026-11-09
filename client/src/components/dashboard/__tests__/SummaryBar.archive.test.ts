import { enableAutoUnmount, flushPromises, mount } from "@vue/test-utils";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import SummaryBar from "@/components/dashboard/SummaryBar.vue";
import { countsWithoutArchiving } from "@/lib/needs-you";
import { useArchiveQueueStore } from "@/stores/archive-queue";
import { useSessionsStore } from "@/stores/sessions";

vi.mock("@tanstack/vue-router", () => ({ useRouter: () => ({ navigate: vi.fn() }) }));

enableAutoUnmount(afterEach);

function item(id: string, status: string) {
  return {
    session: { id, title: id, directory: "/repos/lighthouse", time: { created: 1, updated: 2 } },
    instanceId: `inst-${id}`,
    sessionStatus: status,
    activityStatus: status === "active" ? "busy" : "idle",
    retentionStatus: "active",
    lifecycleStatus: "completed",
    workspaceDirectory: "/repos/lighthouse",
  };
}

describe("countsWithoutArchiving", () => {
  it("takes a pending session off Working if it's in a turn, else off Idle", () => {
    const sessions = [item("a", "active"), item("b", "idle"), item("c", "waiting_input")] as never;
    expect(countsWithoutArchiving({ active: 2, idle: 1 }, sessions, new Set(["a", "b"]))).toEqual({ active: 1, idle: 0 });
    expect(countsWithoutArchiving({ active: 2, idle: 1 }, sessions, new Set())).toEqual({ active: 2, idle: 1 });
  });
});

describe("the dashboard's summary counts and a queued archive", () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    localStorage.clear();
    setActivePinia(createPinia());
    const json = (body: unknown) => new Response(JSON.stringify(body), { status: 200, headers: { "Content-Type": "application/json" } });
    vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL) => {
      const url = input instanceof Request ? input.url : String(input);
      if (url.includes("/api/fleet/summary")) return json({ activeSessions: 0, idleSessions: 3, totalTokens: 0, totalCost: 0, queuedTasks: 0 });
      return json([]);
    }));
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it("drops a session from Idle as soon as it's archived, and puts it back on Undo", async () => {
    const wrapper = mount(SummaryBar);
    await flushPromises();
    useSessionsStore().setSessions([item("a-1", "idle"), item("a-2", "idle"), item("a-3", "idle")] as never);
    await flushPromises();
    const idle = () => wrapper.get('[data-testid="summary-idle-count"]').text();
    expect(idle()).toContain("3");

    const queue = useArchiveQueueStore();
    queue.archive(["a-1"]);
    await flushPromises();
    expect(idle()).toContain("2");

    queue.undo();
    await flushPromises();
    expect(idle()).toContain("3");
  });
});
