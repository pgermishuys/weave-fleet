import { enableAutoUnmount, flushPromises, mount } from "@vue/test-utils";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import FleetDashboard from "@/components/dashboard/FleetDashboard.vue";

vi.mock("@tanstack/vue-router", () => ({ useRouter: () => ({ navigate: vi.fn() }) }));

enableAutoUnmount(afterEach);

const json = (body: unknown) => new Response(JSON.stringify(body), { status: 200, headers: { "Content-Type": "application/json" } });

describe("the dashboard's harness banner", () => {
  beforeEach(() => {
    localStorage.clear();
    setActivePinia(createPinia());
    vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL) => {
      const url = input instanceof Request ? input.url : String(input);
      if (url.includes("/api/harnesses")) {
        return json([{ type: "opencode2", displayName: "OpenCode", available: false, userEnabled: true, reason: "The opencode program isn't on this machine's PATH." }]);
      }
      return json([]);
    }));
  });

  afterEach(() => vi.unstubAllGlobals());

  it("says why no harness is ready", async () => {
    const wrapper = mount(FleetDashboard);
    await flushPromises();
    expect(wrapper.get("[data-testid='harness-setup-banner']").text()).toContain("The opencode program isn't on this machine's PATH.");
  });
});
