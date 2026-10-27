import { flushPromises, mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { beforeEach, describe, expect, it, vi } from "vitest";

const navigate = vi.hoisted(() => vi.fn());
vi.mock("@tanstack/vue-router", () => ({
  useRouter: () => ({ navigate }),
  createFileRoute: () => (options: unknown) => options,
}));
vi.mock("@/components/dashboard/FleetDashboard.vue", () => ({ default: { render: () => null } }));

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { "Content-Type": "application/json" } });
}

/** Fleet opening at `path`, with the switch on or off: a fresh module, as at app start. */
async function openAt(path: string, switchOn: boolean) {
  window.history.pushState({}, "", path);
  vi.resetModules();
  setActivePinia(createPinia());
  vi.stubGlobal("fetch", vi.fn(async () => json(switchOn ? { "features.mission-control.enabled": "true" } : {})));
  const { HomePage } = await import("@/routes/index");
  const mountHome = async () => {
    const wrapper = mount(HomePage);
    await flushPromises();
    wrapper.unmount();
  };
  await mountHome();
  return mountHome;
}

describe("landing on mission control", () => {
  beforeEach(() => navigate.mockClear());

  it("lands on /fleet when Fleet opens at / with the switch on", async () => {
    await openAt("/", true);

    expect(navigate).toHaveBeenCalledWith({ to: "/fleet", replace: true });
  });

  it("stays on / with the switch off", async () => {
    await openAt("/", false);

    expect(navigate).not.toHaveBeenCalled();
  });

  it("leaves a deep link alone, and a later visit to / from it", async () => {
    const visitHome = await openAt("/sessions/abc", true);
    await visitHome();

    expect(navigate).not.toHaveBeenCalled();
  });

  it("lets Sessions reach / after landing, without landing again", async () => {
    const visitHome = await openAt("/", true);
    expect(navigate).toHaveBeenCalledTimes(1);

    await visitHome();

    expect(navigate).toHaveBeenCalledTimes(1);
  });
});
