import { flushPromises, mount } from "@vue/test-utils";
import { ref } from "vue";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import JobsNavPanel from "@/components/jobs/JobsNavPanel.vue";
import { setActiveMachine } from "@/lib/machines";
import type { Job } from "@/lib/jobs";

const { navigate, pathname } = vi.hoisted(() => ({ navigate: vi.fn(), pathname: { value: "/jobs" } }));

vi.mock("@tanstack/vue-router", () => ({
  useRouter: () => ({ navigate }),
  useLocation: () => ref(pathname.value),
}));

const base: Job = {
  id: "", title: "", ask: "Do it", repository: "https://example.com/acme/ledger-api", labels: [], pinnedMachineId: null,
  state: "waiting", machineId: null, machineName: null, sessionId: null, whyWaiting: null, error: null, createdAt: "2026-10-27T08:00:00Z",
};

describe("the jobs list", () => {
  beforeEach(() => {
    localStorage.clear();
    setActiveMachine(null);
    setActivePinia(createPinia());
    navigate.mockClear();
    pathname.value = "/jobs";
    const jobs: Job[] = [
      { ...base, id: "j1", title: "Add rate limits", state: "running" },
      { ...base, id: "j2", title: "Fix rounding", state: "needs_you" },
      { ...base, id: "j3", title: "Update the docs" },
      { ...base, id: "j4", title: "Build the app", state: "done" },
      { ...base, id: "j5", title: "Bump deps", state: "failed" },
    ];
    vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ jobs, machines: [] }), { status: 200, headers: { "Content-Type": "application/json" } })));
  });

  afterEach(() => vi.unstubAllGlobals());

  it("groups jobs Running, Queued and Done, a job that needs you staying Running and a failed one Done", async () => {
    const wrapper = mount(JobsNavPanel);
    await flushPromises();

    const titles = (group: string) => wrapper.findAll(`[data-jobs-group='${group}'] .jobs-nav__title`).map((t) => t.text());
    expect(titles("running")).toEqual(["Add rate limits", "Fix rounding"]);
    expect(titles("queued")).toEqual(["Update the docs"]);
    expect(titles("done")).toEqual(["Build the app", "Bump deps"]);
    expect(wrapper.find("[data-job-id='j2']").text()).toContain("Needs you");
    expect(wrapper.find("[data-job-id='j5']").text()).toContain("Failed");
  });

  it("opens a job at its own route and marks the one the address names", async () => {
    pathname.value = "/jobs/j3";
    const wrapper = mount(JobsNavPanel);
    await flushPromises();

    expect(wrapper.find("[data-job-id='j3']").attributes("aria-current")).toBe("page");
    await wrapper.find("[data-job-id='j1']").trigger("click");
    expect(navigate).toHaveBeenCalledWith({ to: "/jobs/j1" });
    navigate.mockClear();
    await wrapper.find("[data-job-id='j3']").trigger("click");
    expect(navigate).not.toHaveBeenCalled();
  });
});
