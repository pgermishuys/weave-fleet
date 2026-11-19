import { enableAutoUnmount, flushPromises, mount } from "@vue/test-utils";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { setActiveMachine } from "@/lib/machines";
import FirstRunFleet from "@/components/onboarding/FirstRunFleet.vue";

enableAutoUnmount(afterEach);

const atlas = { machineId: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", name: "atlas", os: "linux", baseUrl: "http://192.168.1.40:6262", hint: "7K3M", source: "Home network", expiresAt: "2026-11-19T18:00:00.000Z" };
const atlasMachine = { id: atlas.machineId, name: "atlas", baseUrl: atlas.baseUrl, os: "linux", status: "online", addedAt: "2026-11-19T17:00:00.000Z", lastSeenAt: null, token: "fdt_d1.secret" };

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

const DONE_KEY = "fleet.first-run-fleet.v1";
const inBody = (selector: string): Element | null => document.body.querySelector(selector);

describe("The desktop app's first run", () => {
  let routes: Record<string, (url: string, init?: RequestInit) => Response>;

  beforeEach(() => {
    localStorage.clear();
    setActiveMachine(null);
    setActivePinia(createPinia());
    routes = { "/api/machines": () => json({ machines: [] }), "/api/machines/found": () => json({ machines: [atlas] }) };
    vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const route = routes[String(input).split("?")[0]];
      return route ? route(String(input), init) : json({ error: "no route" }, 404);
    }));
    (window as unknown as { fleetDesktop: unknown }).fleetDesktop = { getUpdateState: () => Promise.resolve({}) };
  });

  afterEach(() => {
    delete (window as unknown as { fleetDesktop?: unknown }).fleetDesktop;
    setActiveMachine(null);
    vi.unstubAllGlobals();
  });

  it("finds a waiting Fleet, joins it, then asks where new sessions should run", async () => {
    routes["/api/machines/pair"] = () => json(atlasMachine);
    mount(FirstRunFleet, { attachTo: document.body, global: { stubs: { teleport: false } } });
    await flushPromises();

    expect(inBody("[data-testid='first-run-fleet']")?.textContent).toContain("Found your Fleet");
    const code = inBody("[data-testid='machines-found-code']") as HTMLInputElement;
    code.value = "fh1q";
    code.dispatchEvent(new Event("input"));
    await flushPromises();
    (inBody("[data-testid='machines-found-add']") as HTMLButtonElement).click();
    await flushPromises();

    expect(inBody("[data-testid='first-run-fleet']")?.textContent).toContain("Where should new sessions run?");
    expect(inBody("[data-testid='first-run-fleet']")?.textContent).toContain("atlas joined your Fleet");
    (document.body.querySelectorAll("[data-testid='first-run-machine']")[0] as HTMLButtonElement).click();
    await flushPromises();

    expect(localStorage.getItem(DONE_KEY)).toBe("true");
    expect(inBody("[data-testid='first-run-fleet']")).toBeNull();
  });

  it("shows nothing and doesn't look again when no machine is waiting", async () => {
    routes["/api/machines/found"] = () => json({ machines: [] });
    mount(FirstRunFleet, { attachTo: document.body, global: { stubs: { teleport: false } } });
    await flushPromises();

    expect(inBody("[data-testid='first-run-fleet']")).toBeNull();
    expect(localStorage.getItem(DONE_KEY)).toBe("true");
  });

  it("stays out of a browser tab, and out of the way once it has run", async () => {
    delete (window as unknown as { fleetDesktop?: unknown }).fleetDesktop;
    mount(FirstRunFleet, { attachTo: document.body, global: { stubs: { teleport: false } } });
    await flushPromises();
    expect(inBody("[data-testid='first-run-fleet']")).toBeNull();

    (window as unknown as { fleetDesktop: unknown }).fleetDesktop = { getUpdateState: () => Promise.resolve({}) };
    localStorage.setItem(DONE_KEY, "true");
    mount(FirstRunFleet, { attachTo: document.body, global: { stubs: { teleport: false } } });
    await flushPromises();
    expect(inBody("[data-testid='first-run-fleet']")).toBeNull();
  });
});
