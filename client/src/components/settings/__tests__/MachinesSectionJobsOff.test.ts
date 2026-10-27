import { enableAutoUnmount, flushPromises, mount } from "@vue/test-utils";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { saveMachines, setActiveMachine, type MachineConnection } from "@/lib/machines";
import MachinesSection from "@/components/settings/MachinesSection.vue";

vi.mock("@tanstack/vue-router", () => ({ useRouter: () => ({ navigate: vi.fn() }) }));

enableAutoUnmount(afterEach);

const mini: MachineConnection = {
  id: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
  name: "mini",
  baseUrl: "http://100.64.12.40:2113",
  token: "mini-token-0123456789",
  os: "macos",
  addedAt: "2026-10-01T00:00:00.000Z",
};

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

/** Jobs is an Experimental switch: with it off, Settings → Machines is exactly what it was and asks for nothing new. */
describe("Settings → Machines with Jobs off", () => {
  let requests: string[];

  beforeEach(() => {
    localStorage.clear();
    sessionStorage.clear();
    setActiveMachine(null);
    setActivePinia(createPinia());
    saveMachines([mini]);
    requests = [];
    vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL) => {
      const url = input instanceof Request ? input.url : String(input);
      requests.push(url);
      if (url.endsWith("/api/machine")) {
        return json({
          id: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
          name: "atlas",
          hostName: "atlas",
          os: "linux",
          version: "0.48.0",
          apiVersion: 1,
          authMode: "token",
          remoteReachable: true,
          requiresToken: false,
        });
      }
      return json({}, 404);
    }));
  });

  afterEach(() => {
    setActiveMachine(null);
    vi.unstubAllGlobals();
  });

  it("renders as it always did and asks nothing about jobs", async () => {
    const wrapper = mount(MachinesSection);
    await flushPromises();

    expect(wrapper.html()).toMatchSnapshot();
    expect(requests.filter((url) => url.includes("/api/jobs"))).toEqual([]);
  });
});
