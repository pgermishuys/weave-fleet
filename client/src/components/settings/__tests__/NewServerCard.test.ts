import { enableAutoUnmount, flushPromises, mount } from "@vue/test-utils";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { setActiveMachine } from "@/lib/machines";
import NewServerCard from "@/components/settings/NewServerCard.vue";

enableAutoUnmount(afterEach);

const NOW = new Date("2026-11-19T10:00:00.000Z");
const hangarMachine = { id: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", name: "hangar", baseUrl: "http://100.64.0.7:6262", os: "linux", status: "online", addedAt: "2026-11-19T09:00:00.000Z", lastSeenAt: null, token: "fdt_d2.secret" };
const atlasMachine = { id: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", name: "atlas", baseUrl: "http://100.64.0.9:6262", os: "linux", status: "online", addedAt: "2026-11-19T10:01:00.000Z", lastSeenAt: null, token: "fdt_d1.secret" };

function joinCode(expiresAt = new Date(NOW.getTime() + 30 * 60_000)) {
  return {
    code: "J8RT-4WXN",
    expiresAt: expiresAt.toISOString(),
    addresses: ["http://100.64.0.2:6262", "http://192.168.1.20:6262"],
    target: "http://100.64.0.2:6262,http://192.168.1.20:6262#J8RT-4WXN",
    machineName: "studio",
  };
}

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

describe("Add a machine → On a new server", () => {
  let routes: Record<string, (url: string, init?: RequestInit) => Response>;
  let made: number;
  let listed: unknown[];
  let written: string[];

  beforeEach(() => {
    vi.useFakeTimers({ now: NOW, toFake: ["Date", "setTimeout", "clearTimeout", "setInterval", "clearInterval"] });
    localStorage.clear();
    setActiveMachine(null);
    setActivePinia(createPinia());
    made = 0;
    listed = [hangarMachine];
    written = [];
    routes = {
      "/api/machines": () => json({ machines: listed }),
      "/api/machines/join-code": () => { made++; return json(joinCode()); },
    };
    vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const route = routes[String(input).split("?")[0]];
      return route ? route(String(input), init) : json({ error: "no route" }, 404);
    }));
    Object.defineProperty(navigator, "clipboard", { value: { writeText: vi.fn(async (text: string) => { written.push(text); }) }, configurable: true });
  });

  afterEach(() => {
    setActiveMachine(null);
    vi.unstubAllGlobals();
    vi.useRealTimers();
  });

  it("shows one line to paste, with this machine's addresses and a one-time code", async () => {
    const wrapper = mount(NewServerCard);
    await flushPromises();

    expect(made).toBe(1);
    expect(wrapper.get("[data-testid='machines-new-server-line']").text())
      .toBe("curl -fsSL https://github.com/pgermishuys/weave-fleet/releases/latest/download/install.sh | sh -s -- --join 'http://100.64.0.2:6262,http://192.168.1.20:6262#J8RT-4WXN'");
    expect(wrapper.get("[data-testid='machines-new-server-warning']").text()).toContain("Works once, for 30 minutes");
    expect(wrapper.get("[data-testid='machines-new-server-warning']").text()).toContain("treat it like a password");
  });

  it("counts down to when the code stops working", async () => {
    const wrapper = mount(NewServerCard);
    await flushPromises();
    expect(wrapper.get("[data-testid='machines-new-server-waiting']").text()).toContain("expires in 30 min");

    await vi.advanceTimersByTimeAsync(10 * 60_000);
    expect(wrapper.get("[data-testid='machines-new-server-waiting']").text()).toContain("expires in 20 min");

    await vi.advanceTimersByTimeAsync(20 * 60_000 - 30_000);
    expect(wrapper.get("[data-testid='machines-new-server-waiting']").text()).toContain("expires in 30 s");
  });

  it("copies the whole line", async () => {
    const wrapper = mount(NewServerCard);
    await flushPromises();

    await wrapper.get("[data-testid='machines-new-server-copy']").trigger("click");
    await flushPromises();

    expect(written).toEqual([wrapper.get("[data-testid='machines-new-server-line']").text()]);
    expect(wrapper.get("[data-testid='machines-new-server-copy']").text()).toBe("Copied");
  });

  it("says the code has expired, stops offering the line and makes a new one on request", async () => {
    const wrapper = mount(NewServerCard);
    await flushPromises();

    await vi.advanceTimersByTimeAsync(30 * 60_000 + 1000);
    expect(wrapper.get("[data-testid='machines-new-server-expired']").text()).toContain("This code has expired");
    expect(wrapper.get("[data-testid='machines-new-server-copy']").attributes("disabled")).toBeDefined();
    expect(wrapper.find("[data-testid='machines-new-server-waiting']").exists()).toBe(false);

    routes["/api/machines/join-code"] = () => { made++; return json({ ...joinCode(new Date(Date.now() + 30 * 60_000)), code: "K2MQ-9PDV", target: "http://100.64.0.2:6262#K2MQ-9PDV" }); };
    await wrapper.get("[data-testid='machines-new-server-renew']").trigger("click");
    await flushPromises();

    expect(made).toBe(2);
    expect(wrapper.get("[data-testid='machines-new-server-line']").text()).toContain("#K2MQ-9PDV");
    expect(wrapper.find("[data-testid='machines-new-server-expired']").exists()).toBe(false);
    expect(wrapper.get("[data-testid='machines-new-server-copy']").attributes("disabled")).toBeUndefined();
  });

  it("says so when a new machine joins, and names it", async () => {
    const wrapper = mount(NewServerCard);
    await flushPromises();
    await vi.advanceTimersByTimeAsync(3500);
    expect(wrapper.emitted("joined")).toBeUndefined();

    listed = [hangarMachine, atlasMachine];
    await vi.advanceTimersByTimeAsync(3500);
    await flushPromises();

    expect(wrapper.emitted("joined")).toEqual([["atlas"]]);
  });

  it("does not take a machine that was already there for the one that joined", async () => {
    const wrapper = mount(NewServerCard);
    await flushPromises();

    await vi.advanceTimersByTimeAsync(9000);
    await flushPromises();

    expect(wrapper.emitted("joined")).toBeUndefined();
  });

  it("explains a machine that only listens on itself and can try again", async () => {
    routes["/api/machines/join-code"] = () => json({ error: "studio only listens on this machine, so a new server can't reach it." }, 400);
    const wrapper = mount(NewServerCard);
    await flushPromises();

    expect(wrapper.find("[data-testid='machines-new-server-line']").exists()).toBe(false);
    expect(wrapper.get("[data-testid='machines-new-server-error']").text()).toContain("only listens on this machine");

    routes["/api/machines/join-code"] = () => json(joinCode());
    await wrapper.get("[data-testid='machines-new-server-error'] button").trigger("click");
    await flushPromises();

    expect(wrapper.find("[data-testid='machines-new-server-line']").exists()).toBe(true);
  });

  it("stops looking when it is closed", async () => {
    const wrapper = mount(NewServerCard);
    await flushPromises();
    wrapper.unmount();
    const calls = (fetch as ReturnType<typeof vi.fn>).mock.calls.length;

    await vi.advanceTimersByTimeAsync(20_000);

    expect((fetch as ReturnType<typeof vi.fn>).mock.calls.length).toBe(calls);
  });
});
