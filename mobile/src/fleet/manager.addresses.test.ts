import { afterEach, beforeEach, describe, expect, test } from "bun:test";
import type { ConnectionState, MachineHub } from "./hub";
import { FleetManager } from "./manager";
import { MachinesStore, type KeyValueStorage, type Machine } from "./machines-core";
import type { MachineAddress, Probe } from "./routes-core";

const TS = "https://studio-desktop.tail4f2a.ts.net";
const TS_IP = "http://100.88.14.3:2113";
const LAN = "http://192.168.1.42:2113";
const ID = "studio";

/** Which addresses answer the probe, which hold a connection, and what the phone asked for. */
let answering: Set<string>;
let holding: Set<string>;
let requests: { url: string; method: string; body?: string }[];
const realFetch = globalThis.fetch;

class FakeHub {
  state: ConnectionState = "offline";
  url = "";
  moves: string[] = [];
  constructor(
    private route: { baseUrl: string },
    private onState: (state: ConnectionState) => void,
    readonly onLive: () => void,
    private beforeOpen: () => Promise<void>,
  ) {}
  start() {
    void (async () => {
      await this.beforeOpen();
      this.url = this.route.baseUrl;
      this.state = holding.has(this.url) ? "connected" : "offline";
      this.onState(this.state);
      if (this.state === "connected") this.onLive();
    })();
  }
  async stop() {}
  markSignedOut() {}
  subscribeSessions() {
    return () => {};
  }
  async moveTo(url: string) {
    this.moves.push(url);
    if (!holding.has(url)) return false;
    this.url = url;
    return true;
  }
}

function memory(): KeyValueStorage {
  const data = new Map<string, string>();
  return { get: async (k) => data.get(k) ?? null, set: async (k, v) => void data.set(k, v), remove: async (k) => void data.delete(k) };
}

const address = (url: string, kind: MachineAddress["kind"], learned = true): MachineAddress => ({ url, kind, learned });
const machine = (id: string, baseUrl: string, addresses: MachineAddress[], name = id): Machine => (
  { machineId: id, machineName: name, baseUrl, addresses, token: `${id}-key`, deviceId: "d", pairedAt: 1 }
);

async function until(check: () => boolean, ms = 2000) {
  const end = Date.now() + ms;
  while (!check()) {
    if (Date.now() > end) throw new Error("timed out");
    await new Promise((r) => setTimeout(r, 5));
  }
}

const probe = (): Probe => async (url) => (answering.has(url) ? "ok" : "no-answer");

async function start(machines: Machine[]) {
  const store = new MachinesStore(memory());
  for (const m of machines) await store.add(m);
  const hubs = new Map<string, FakeHub>();
  const manager = new FleetManager(store, probe, (route, onState, onLive, beforeOpen) => {
    const hub = new FakeHub(route, onState, onLive, beforeOpen!);
    hubs.set((route as { token: string }).token.replace(/-key$/, ""), hub);
    return hub as unknown as MachineHub;
  });
  return { store, manager, hub: (id = ID) => hubs.get(id)! };
}

beforeEach(() => {
  answering = new Set();
  holding = new Set();
  requests = [];
  globalThis.fetch = (async (input: string, init?: RequestInit) => {
    const url = String(input);
    requests.push({ url, method: init?.method ?? "GET", body: init?.body as string | undefined });
    const base = [TS, TS_IP, LAN, "https://hangar.tail4f2a.ts.net"].find((b) => url.startsWith(b));
    if (!base || !holding.has(base)) throw new TypeError("Network request failed");
    const path = url.slice(base.length);
    const json = (body: unknown) => new Response(JSON.stringify(body), { headers: { "content-type": "application/json" } });
    if (path.startsWith("/api/sessions")) return json([]);
    if (path === "/api/machine") return json({ id: ID, addresses: [{ url: TS_IP, kind: "tailnet-ip" }, { url: LAN, kind: "lan" }] });
    if (path === "/api/machines") return json({ machines: [{ id: ID, addresses: [{ url: LAN }, { url: TS_IP }] }] });
    if (path.startsWith("/api/machines/") && init?.method === "PUT") return json({});
    return new Response(null, { status: 404 });
  }) as typeof fetch;
});

afterEach(() => {
  globalThis.fetch = realFetch;
});

describe("a machine with several addresses", () => {
  const list = [address(TS, "tailnet", false), address(TS_IP, "tailnet-ip"), address(LAN, "lan")];

  test("with Tailscale down it connects on the local network, and the probes never carry the key", async () => {
    answering.add(LAN);
    holding.add(LAN);
    const { manager, store, hub } = await start([machine(ID, TS, list)]);
    await until(() => hub().state === "connected");
    expect(hub().url).toBe(LAN);
    expect(store.get(ID)?.baseUrl).toBe(LAN);
    const view = manager.snapshot().machines[0];
    expect(view.baseUrl).toBe(LAN);
    expect(view.addresses.map((a) => a.lastResult)).toEqual(["no-answer", "no-answer", "ok"]);
    // Every request with the key went to the address that answered as this machine.
    expect(requests.every((r) => r.url.startsWith(LAN))).toBe(true);
  });

  test("moves back to Tailscale only once a connection there holds", async () => {
    answering.add(LAN);
    holding.add(LAN);
    const { store, hub } = await start([machine(ID, TS, list)]);
    await until(() => hub().state === "connected");

    // Tailscale answers the probe but won't hold a connection: the working one stays.
    answering.add(TS);
    hub().onLive();
    await until(() => hub().moves.length === 1);
    await new Promise((r) => setTimeout(r, 20));
    expect(hub().url).toBe(LAN);
    expect(store.get(ID)?.baseUrl).toBe(LAN);

    // Now it holds.
    holding.add(TS);
    hub().onLive();
    await until(() => store.get(ID)?.baseUrl === TS);
    expect(hub().moves).toEqual([TS, TS]);
    expect(hub().url).toBe(TS);
  });

  test("on the first address nothing is probed for a better one", async () => {
    answering.add(TS);
    holding.add(TS);
    const { hub } = await start([machine(ID, TS, list)]);
    await until(() => hub().state === "connected");
    hub().onLive();
    await new Promise((r) => setTimeout(r, 20));
    expect(hub().moves).toEqual([]);
  });

  test("learns the addresses the machine reports, after the one it paired at", async () => {
    answering.add(TS);
    holding.add(TS);
    const { store, hub } = await start([machine(ID, TS, [address(TS, "tailnet", false)])]);
    await until(() => hub().state === "connected");
    await until(() => (store.get(ID)?.addresses?.length ?? 0) === 3);
    expect(store.get(ID)?.addresses?.map((a) => a.url)).toEqual([TS, TS_IP, LAN]);
  });

  test("a read that can't reach the machine picks again and goes once more", async () => {
    answering.add(TS);
    holding.add(TS);
    const { manager, store, hub } = await start([machine(ID, TS, list)]);
    await until(() => hub().state === "connected");
    holding.delete(TS);
    answering.delete(TS);
    holding.add(LAN);
    answering.add(LAN);
    await manager.refresh();
    expect(store.get(ID)?.baseUrl).toBe(LAN);
    expect(requests.at(-1)?.url).toBe(`${LAN}/api/sessions?limit=100&offset=0`);
  });
});

describe("the owner's order", () => {
  const HOME = "hangar";
  const HOME_URL = "https://hangar.tail4f2a.ts.net";
  const list = [address(TS, "tailnet", false), address(TS_IP, "tailnet-ip"), address(LAN, "lan")];

  test("without a home it's kept on the phone", async () => {
    answering.add(TS);
    holding.add(TS);
    const { manager, store, hub } = await start([machine(ID, TS, list)]);
    await until(() => hub().state === "connected");
    await manager.reorder(ID, [LAN, TS, TS_IP]);
    expect(store.get(ID)?.addresses?.map((a) => a.url)).toEqual([LAN, TS, TS_IP]);
    expect(store.get(ID)?.ownOrder).toBe(true);
    expect(requests.some((r) => r.method === "PUT")).toBe(false);
  });

  test("takes home's order, and saves a new one on home", async () => {
    for (const url of [TS, HOME_URL]) {
      answering.add(url);
      holding.add(url);
    }
    const { manager, store, hub } = await start([machine(HOME, HOME_URL, [address(HOME_URL, "tailnet", false)]), machine(ID, TS, list)]);
    await until(() => hub(HOME).state === "connected" && hub().state === "connected");
    hub().onLive();
    // Home lists the local network above Tailscale's IP; the https address the phone paired at keeps its place.
    await until(() => store.get(ID)?.addresses?.[1]?.url === LAN);
    expect(store.get(ID)?.addresses?.map((a) => a.url)).toEqual([TS, LAN, TS_IP]);

    await manager.reorder(ID, [TS, TS_IP, LAN]);
    const put = requests.find((r) => r.method === "PUT");
    expect(put?.url).toBe(`${HOME_URL}/api/machines/${ID}`);
    expect(JSON.parse(put!.body!)).toEqual({ addresses: [TS_IP, LAN] });
    expect(store.get(ID)?.ownOrder).toBe(false);
  });

  test("a list that changed meanwhile is turned away", async () => {
    answering.add(TS);
    holding.add(TS);
    const { manager, hub } = await start([machine(ID, TS, list)]);
    await until(() => hub().state === "connected");
    await expect(manager.reorder(ID, [LAN, TS])).rejects.toThrow("changed meanwhile");
  });
});
