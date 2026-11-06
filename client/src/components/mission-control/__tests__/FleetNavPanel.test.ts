import { flushPromises, mount } from "@vue/test-utils";
import { computed, ref } from "vue";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import FleetNavPanel from "@/components/mission-control/FleetNavPanel.vue";
import { useMachinesStore } from "@/stores/machines";
import { saveMachines, setActiveMachine } from "@/lib/machines";

const { navigate, pathname } = vi.hoisted(() => ({ navigate: vi.fn(), pathname: { value: "/fleet" } }));

vi.mock("@tanstack/vue-router", () => ({
  useRouter: () => ({ navigate }),
  useLocation: () => ref(pathname.value),
}));
vi.mock("@/composables/use-shared-mission-control", () => ({
  useSharedMissionControl: () => ({
    waiting: computed(() => [{ key: "a" }, { key: "b" }]),
    tiles: computed(() => [
      { machineId: "home" }, { machineId: "home" }, { machineId: "m2" },
    ]),
    states: computed(() => [{ id: "m2", status: "unreachable" }]),
    summary: computed(() => ({ running: 0, helpers: 0, needYou: 2 })),
  }),
}));

describe("the fleet menu", () => {
  beforeEach(() => {
    localStorage.clear();
    setActiveMachine(null);
    setActivePinia(createPinia());
    navigate.mockClear();
    pathname.value = "/fleet";
    saveMachines([{ id: "m2", name: "mini", baseUrl: "http://mini.test:5732", token: "t", addedAt: "2026-10-27T08:00:00Z" }]);
    vi.stubGlobal("fetch", vi.fn(async () => new Response("{}", { status: 404 })));
  });

  it("lists Needs you first, then one entry per machine, with counts", async () => {
    const wrapper = mount(FleetNavPanel);
    await flushPromises();

    const groups = wrapper.findAll("[role=group]");
    expect(groups.map((group) => group.attributes("aria-label"))).toEqual(["Attention", "Machines"]);
    expect(groups[0].findAll("button").map((b) => b.text())).toEqual(["Needs you2"]);
    expect(groups[1].findAll("button").map((b) => b.text())).toEqual(["This machine2", "minioffline1"]);
    expect(useMachinesStore().entries).toHaveLength(2);
  });

  it("marks Needs you on /fleet and a machine on its own route", () => {
    expect(mount(FleetNavPanel).find("[aria-current=page]").attributes("data-fleet-view")).toBe("needs-you");

    pathname.value = "/fleet/machines/m2";
    expect(mount(FleetNavPanel).find("[aria-current=page]").attributes("data-fleet-view")).toBe("machine:m2");
  });

  it("opens a view by its own route, and does nothing for the one already open", async () => {
    const wrapper = mount(FleetNavPanel);

    await wrapper.find("[data-fleet-view='machine:m2']").trigger("click");
    expect(navigate).toHaveBeenLastCalledWith({ to: "/fleet/machines/m2" });

    navigate.mockClear();
    await wrapper.find("[data-fleet-view=needs-you]").trigger("click");
    expect(navigate).not.toHaveBeenCalled();
  });
});
