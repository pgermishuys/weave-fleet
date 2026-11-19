import { enableAutoUnmount, flushPromises, mount } from "@vue/test-utils";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { setActiveMachine } from "@/lib/machines";
import FoundMachinesCard from "@/components/settings/FoundMachinesCard.vue";

enableAutoUnmount(afterEach);

const atlas = {
  machineId: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
  name: "atlas",
  os: "linux",
  baseUrl: "http://192.168.1.40:6262",
  hint: "7K3M",
  source: "Home network",
  expiresAt: "2026-11-19T18:00:00.000Z",
};
const atlasMachine = { id: atlas.machineId, name: "atlas", baseUrl: atlas.baseUrl, os: "linux", status: "online", addedAt: "2026-11-19T17:00:00.000Z", lastSeenAt: null, token: "fdt_d1.secret" };

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

describe("Add a machine → Found", () => {
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
  });

  afterEach(() => {
    setActiveMachine(null);
    vi.unstubAllGlobals();
  });

  it("lists a waiting machine with where it was seen and the start of its code", async () => {
    const wrapper = mount(FoundMachinesCard);
    await flushPromises();

    expect(wrapper.get("[data-testid='machines-found-name']").text()).toBe("atlas · Linux");
    expect(wrapper.get("[data-testid='machines-found-row']").text()).toContain("On your home network");
    expect(wrapper.get("[data-testid='machines-found-hint']").text()).toBe("7K3M-");
    expect(wrapper.get("[data-testid='machines-found-add']").attributes("disabled")).toBeDefined();
  });

  it("adds the machine only when the rest of the code is typed, and sends the whole code", async () => {
    let sent: unknown = null;
    routes["/api/machines/pair"] = (_, init) => {
      sent = JSON.parse(String(init?.body));
      return json(atlasMachine);
    };
    const wrapper = mount(FoundMachinesCard);
    await flushPromises();

    await wrapper.get("[data-testid='machines-found-code']").setValue("q9z");
    expect(wrapper.get("[data-testid='machines-found-add']").attributes("disabled")).toBeDefined();
    await wrapper.get("[data-testid='machines-found-code']").setValue("q9zd");
    expect(wrapper.get("[data-testid='machines-found-add']").attributes("disabled")).toBeUndefined();

    await wrapper.get("form").trigger("submit");
    await flushPromises();

    expect(sent).toEqual({ baseUrl: atlas.baseUrl, code: "7K3M-Q9ZD" });
    expect(wrapper.emitted("added")).toEqual([["atlas"]]);
  });

  it("takes the whole code too, but not one that starts with something else", async () => {
    const wrapper = mount(FoundMachinesCard);
    await flushPromises();

    await wrapper.get("[data-testid='machines-found-code']").setValue("1111-2222");
    expect(wrapper.get("[data-testid='machines-found-add']").attributes("disabled")).toBeDefined();
    await wrapper.get("[data-testid='machines-found-code']").setValue("7k3m-q9zd");
    expect(wrapper.get("[data-testid='machines-found-add']").attributes("disabled")).toBeUndefined();
  });

  it("says why a machine could not be added, and adds nothing", async () => {
    routes["/api/machines/pair"] = () => json({ error: "That code has expired or was already used." }, 400);
    const wrapper = mount(FoundMachinesCard);
    await flushPromises();

    await wrapper.get("[data-testid='machines-found-code']").setValue("q9zd");
    await wrapper.get("form").trigger("submit");
    await flushPromises();

    expect(wrapper.get("[data-testid='machines-found-error']").text()).toContain("expired");
    expect(wrapper.emitted("added")).toBeUndefined();
  });

  it("offers the pasted link when nothing is waiting", async () => {
    routes["/api/machines/found"] = () => json({ machines: [] });
    const wrapper = mount(FoundMachinesCard);
    await flushPromises();

    expect(wrapper.find("[data-testid='machines-found-row']").exists()).toBe(false);
    await wrapper.get("[data-testid='machines-found-paste']").trigger("click");
    expect(wrapper.emitted("usePaste")).toHaveLength(1);
  });
});
