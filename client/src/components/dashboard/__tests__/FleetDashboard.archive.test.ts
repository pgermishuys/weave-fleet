import { enableAutoUnmount, flushPromises, mount } from "@vue/test-utils";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import FleetDashboard from "@/components/dashboard/FleetDashboard.vue";
import { useArchiveQueueStore } from "@/stores/archive-queue";
import { useSessionsStore } from "@/stores/sessions";

vi.mock("@tanstack/vue-router", () => ({ useRouter: () => ({ navigate: vi.fn() }) }));

enableAutoUnmount(afterEach);

function item(id: string, title: string) {
  return {
    session: { id, title, directory: "/repos/lighthouse", time: { created: 1, updated: 2 } },
    instanceId: `inst-${id}`,
    sessionStatus: "idle",
    activityStatus: "idle",
    retentionStatus: "active",
    lifecycleStatus: "completed",
    workspaceDirectory: "/repos/lighthouse",
  };
}

describe("the dashboard's Recent list and a queued archive", () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    localStorage.clear();
    setActivePinia(createPinia());
    const json = (body: unknown) => new Response(JSON.stringify(body), { status: 200, headers: { "Content-Type": "application/json" } });
    vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL) => {
      const url = input instanceof Request ? input.url : String(input);
      return url.includes("/api/sessions") ? json([item("a-1", "Tide chart caching"), item("a-2", "Harbour lights")]) : json([]);
    }));
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it("hides a session as soon as it's archived, and shows it again on Undo", async () => {
    useSessionsStore().setSessions([item("a-1", "Tide chart caching"), item("a-2", "Harbour lights")] as never);
    const wrapper = mount(FleetDashboard);
    await flushPromises();
    expect(wrapper.text()).toContain("Tide chart caching");

    const queue = useArchiveQueueStore();
    queue.archive(["a-1"]);
    await flushPromises();
    expect(wrapper.text()).not.toContain("Tide chart caching");
    expect(wrapper.text()).toContain("Harbour lights");

    queue.undo();
    await flushPromises();
    expect(wrapper.text()).toContain("Tide chart caching");
  });
});
