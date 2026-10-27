import { enableAutoUnmount, flushPromises, mount } from "@vue/test-utils";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import JobsPage from "@/components/pages/JobsPage.vue";
import { setActiveMachine } from "@/lib/machines";
import type { Job, JobMachine } from "@/lib/jobs";

vi.mock("@tanstack/vue-router", () => ({ useRouter: () => ({ navigate: vi.fn() }) }));

enableAutoUnmount(afterEach);

const base: Job = {
  id: "",
  title: "",
  ask: "Do it",
  repository: "https://example.com/acme/ledger-api",
  labels: [],
  pinnedMachineId: null,
  state: "waiting",
  machineId: null,
  machineName: null,
  sessionId: null,
  whyWaiting: null,
  error: null,
  createdAt: "2026-10-27T08:00:00Z",
};
const atlas: JobMachine = { id: "m1", name: "atlas", isLocal: true, seats: 2, used: 1, labels: [], status: "online" };
const mini: JobMachine = { id: "m2", name: "mini", isLocal: false, seats: 1, used: 0, labels: ["mac"], status: "online" };
const laptop: JobMachine = { id: "m3", name: "laptop", isLocal: false, seats: 0, used: 0, labels: [], status: "online" };

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

describe("Jobs screen", () => {
  let jobs: Job[];
  let requests: string[];

  beforeEach(() => {
    localStorage.clear();
    setActiveMachine(null);
    setActivePinia(createPinia());
    jobs = [];
    requests = [];
    vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = input instanceof Request ? input.url : String(input);
      requests.push(`${init?.method ?? "GET"} ${url}`);
      if (url.endsWith("/api/preferences")) return json({ Jobs: "true" });
      if (url.endsWith("/api/jobs")) return json({ jobs, machines: [atlas, mini, laptop] });
      return json({}, 404);
    }));
  });

  afterEach(() => vi.unstubAllGlobals());

  it("says what to do when nothing is queued", async () => {
    const wrapper = mount(JobsPage);
    await flushPromises();

    expect(wrapper.find("[data-testid='jobs-empty']").text()).toContain("No jobs yet");
  });

  it("shows each machine's seats, leaving out one that takes no jobs", async () => {
    const wrapper = mount(JobsPage);
    await flushPromises();

    expect(wrapper.findAll("[data-testid='jobs-seat-chip']").map((chip) => chip.text())).toEqual(["atlas 1/2", "mini 0/1"]);
  });

  it("puts a job in its column, with its machine, and says why a waiting one waits", async () => {
    jobs = [
      { ...base, id: "j1", title: "Add rate limits", state: "running", machineId: "m1", machineName: "atlas", sessionId: "s1" },
      { ...base, id: "j2", title: "Update the docs", whyWaiting: "No machine has a free seat." },
      { ...base, id: "j3", title: "Fix rounding", state: "needs_you", machineId: "m1", machineName: "atlas", sessionId: "s2" },
      { ...base, id: "j4", title: "Build the app", state: "done", machineId: "m2", machineName: "mini", sessionId: "s3" },
    ];
    const wrapper = mount(JobsPage);
    await flushPromises();

    const titlesIn = (column: string) => wrapper.findAll(`[data-testid='jobs-column-${column}'] [data-testid='job-card']`).map((card) => card.text());
    expect(titlesIn("running")[0]).toContain("Add rate limits");
    expect(wrapper.find("[data-testid='jobs-column-running'] [data-testid='job-machine']").text()).toBe("atlas");
    expect(titlesIn("waiting")[0]).toContain("No machine has a free seat.");
    expect(titlesIn("needs_you")[0]).toContain("Fix rounding");
    expect(titlesIn("done")[0]).toContain("Build the app");
  });

  it("opens a card's detail, and cancels only a waiting job", async () => {
    jobs = [
      { ...base, id: "j1", title: "Add rate limits", state: "running", machineId: "m1", machineName: "atlas", sessionId: "s1" },
      { ...base, id: "j2", title: "Update the docs", whyWaiting: "No machine has a free seat." },
    ];
    const wrapper = mount(JobsPage);
    await flushPromises();
    const cards = wrapper.findAll("[data-testid='job-card']");

    await wrapper.find("[data-testid='jobs-column-running'] [data-testid='job-card']").trigger("click");
    expect(wrapper.find("[data-testid='job-open-session']").exists()).toBe(true);
    expect(wrapper.find("[data-testid='job-cancel']").exists()).toBe(false);

    await cards.find((card) => card.text().includes("Update the docs"))!.trigger("click");
    expect(wrapper.find("[data-testid='job-detail-why']").text()).toBe("No machine has a free seat.");
    expect(wrapper.find("[data-testid='job-open-session']").exists()).toBe(false);

    await wrapper.find("[data-testid='job-cancel']").trigger("click");
    await flushPromises();
    expect(requests).toContain("DELETE /api/jobs/j2");
  });

  it("shows each machine's seats on the Agents tab", async () => {
    jobs = [{ ...base, id: "j1", title: "Add rate limits", state: "running", machineId: "m1", machineName: "atlas", sessionId: "s1" }];
    const wrapper = mount(JobsPage);
    await flushPromises();

    await wrapper.find("[data-testid='jobs-tab-agents']").trigger("click");

    const cards = wrapper.findAll("[data-testid='jobs-agent-card']").map((card) => card.text());
    expect(cards[0]).toContain("Add rate limits");
    expect(cards[0]).toContain("Free · takes the next job that fits");
    expect(cards[2]).toContain("Takes no jobs");
  });
});

describe("Jobs switched off", () => {
  beforeEach(() => {
    localStorage.clear();
    setActiveMachine(null);
    setActivePinia(createPinia());
  });

  afterEach(() => vi.unstubAllGlobals());

  it("shows how to turn it on and asks Fleet nothing about jobs", async () => {
    const requests: string[] = [];
    vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL) => {
      const url = input instanceof Request ? input.url : String(input);
      requests.push(url);
      return json({}, url.endsWith("/api/preferences") ? 200 : 404);
    }));

    const wrapper = mount(JobsPage);
    await flushPromises();

    expect(wrapper.find("[data-testid='feature-off']").text()).toContain("Jobs is off");
    expect(requests.filter((url) => url.includes("/api/jobs"))).toEqual([]);
  });
});
