import { enableAutoUnmount, flushPromises, mount } from "@vue/test-utils";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import FleetDashboard from "@/components/dashboard/FleetDashboard.vue";
import { MACHINE_TARGET, targetFor } from "@/lib/machine-target";
import { saveMachines, setActiveMachine, type MachineConnection } from "@/lib/machines";
import { useMachinesStore } from "@/stores/machines";
import { useSessionsStore } from "@/stores/sessions";

const navigate = vi.hoisted(() => vi.fn());
vi.mock("@tanstack/vue-router", () => ({ useRouter: () => ({ navigate }) }));

enableAutoUnmount(afterEach);

const falcon: MachineConnection = {
  id: "ffffffffffffffffffffffffffffffff",
  name: "falcon",
  baseUrl: "http://100.64.90.72:2412",
  token: "falcon-token-0123456789",
  os: "linux",
  addedAt: "2026-10-01T00:00:00.000Z",
};

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { "Content-Type": "application/json" } });
}

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

describe("the dashboard on another machine", () => {
  let requests: string[];

  beforeEach(() => {
    localStorage.clear();
    setActiveMachine(null);
    setActivePinia(createPinia());
    saveMachines([falcon]);
    navigate.mockClear();
    requests = [];
    vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL) => {
      const url = input instanceof Request ? input.url : String(input);
      requests.push(url.split("?")[0]);
      if (url.startsWith(`${falcon.baseUrl}/api/sessions`)) return json([item("f-1", "Lighthouse sensor feed")]);
      if (url.includes("/api/fleet/summary")) return json({ activeSessions: 0, idleSessions: 1, queuedTasks: 0, totalTokens: 0, totalCost: 0 });
      return json([]);
    }));
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("lists that machine's sessions, asks only it, offers no archive or delete, and opens a session on it", async () => {
    useSessionsStore().setSessions([item("k-1", "Tide chart caching")] as never);
    const wrapper = mount(FleetDashboard, { global: { provide: { [MACHINE_TARGET as symbol]: () => targetFor(falcon) } } });
    await flushPromises();

    expect(wrapper.text()).toContain("Lighthouse sensor feed");
    expect(wrapper.text()).not.toContain("Tide chart caching");
    // What the dashboard reads (sessions, summary, spend) goes to that machine alone (the preferences are the page's own).
    for (const path of ["/api/sessions", "/api/fleet/summary", "/api/analytics/daily"]) {
      expect(requests.filter((url) => url.endsWith(path))).toEqual([`${falcon.baseUrl}${path}`]);
    }
    expect(wrapper.find("[data-testid='session-archive-button']").exists()).toBe(false);
    expect(wrapper.find("[data-testid='session-delete-button']").exists()).toBe(false);

    const remember = vi.spyOn(useMachinesStore(), "rememberSessions");
    await wrapper.get("[data-testid='dashboard-session-row']").trigger("click");

    expect(remember).toHaveBeenCalledWith(falcon.id, ["f-1"]);
    expect(navigate).toHaveBeenCalledWith(expect.objectContaining({ to: "/sessions/$id", params: { id: "f-1" } }));
  });
});
