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

const json = (body: unknown) => new Response(JSON.stringify(body), { status: 200, headers: { "Content-Type": "application/json" } });

describe("the dashboard's session row Archive", () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    localStorage.clear();
    setActivePinia(createPinia());
    vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL) => {
      const url = input instanceof Request ? input.url : String(input);
      return url.includes("/api/sessions") ? json([item("a-1", "Tide chart caching")]) : json([]);
    }));
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it("goes through the Undo queue instead of archiving at once", async () => {
    useSessionsStore().setSessions([item("a-1", "Tide chart caching")] as never);
    const wrapper = mount(FleetDashboard);
    await flushPromises();

    await wrapper.get("[data-testid='session-archive-button']").trigger("click");
    await flushPromises();

    expect(useArchiveQueueStore().pending?.ids).toEqual(["a-1"]);
    expect(wrapper.text()).not.toContain("Tide chart caching");
  });
});
