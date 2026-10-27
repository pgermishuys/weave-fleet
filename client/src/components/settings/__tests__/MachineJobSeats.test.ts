import { enableAutoUnmount, flushPromises, mount } from "@vue/test-utils";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import MachineJobSeats from "@/components/settings/MachineJobSeats.vue";
import { HOME_MACHINE_KEY } from "@/lib/machines";

enableAutoUnmount(afterEach);

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

const atlas = { id: "m1", name: "atlas", isLocal: true, seats: 2, used: 0, labels: [] as string[], status: "online" };
const mini = { id: "m2", name: "mini", isLocal: false, seats: 0, used: 0, labels: [] as string[], status: "online" };

describe("Seats for jobs on a machine's card", () => {
  let puts: { url: string; body: unknown }[];

  beforeEach(() => {
    setActivePinia(createPinia());
    puts = [];
    vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (init?.method === "PUT") {
        const body = JSON.parse(String(init.body)) as { seats?: number; labels?: string[] };
        puts.push({ url, body });
        return json({ ...(url.endsWith("/m2") ? mini : atlas), ...body });
      }
      return json({ jobs: [], machines: [atlas, mini] });
    }));
  });

  afterEach(() => vi.unstubAllGlobals());

  it("shows this machine's seats on the home card and saves a change", async () => {
    const wrapper = mount(MachineJobSeats, { props: { entryKey: HOME_MACHINE_KEY } });
    await flushPromises();
    const input = wrapper.find<HTMLInputElement>("[data-testid='machine-job-seats-input']");

    expect(input.element.value).toBe("2");
    await input.setValue("3");
    await flushPromises();

    expect(puts).toEqual([{ url: "/api/jobs/machines/m1", body: { seats: 3 } }]);
  });

  it("gives another machine labels, trimmed and without repeats", async () => {
    const wrapper = mount(MachineJobSeats, { props: { entryKey: "m2" } });
    await flushPromises();
    const input = wrapper.find<HTMLInputElement>("[data-testid='machine-job-labels-input']");

    await input.setValue(" mac, ios ,mac");
    await flushPromises();

    expect(puts).toEqual([{ url: "/api/jobs/machines/m2", body: { labels: ["mac", "ios"] } }]);
  });

  it("draws nothing for a machine the home Fleet doesn't list", async () => {
    const wrapper = mount(MachineJobSeats, { props: { entryKey: "m9" } });
    await flushPromises();

    expect(wrapper.find("[data-testid='machine-job-seats']").exists()).toBe(false);
  });
});
