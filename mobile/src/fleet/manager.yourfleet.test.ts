import { afterEach, beforeEach, describe, expect, test } from "bun:test";
import type { ConnectionState, MachineHub } from "./hub";
import { FleetManager } from "./manager";
import { MachinesStore, type KeyValueStorage, type Machine } from "./machines-core";
import { Roster } from "./your-fleet-core";

const HANGAR = "https://hangar.tail4f2a.ts.net";
const ATLAS = "https://atlas.tail4f2a.ts.net";

/** What hangar says its list is, whether Your Fleet is on there, and what each machine was asked. */
let listed: { id: string; name: string; baseUrl: string; addresses?: { url: string }[] }[];
let yourFleet: boolean;
let atlasAway: boolean;
let requests: { url: string; method: string; auth: string | null }[];
const realFetch = globalThis.fetch;

class FakeHub {
  state: ConnectionState = "offline";
  constructor(private route: { baseUrl: string }, private onState: (s: ConnectionState) => void, readonly onLive: () => void) {}
  start() {
    queueMicrotask(() => {
      this.state = "connected";
      this.onState(this.state);
      this.onLive();
    });
  }
  async stop() {}
  markSignedOut() {}
  subscribeSessions() {
    return () => {};
  }
  async moveTo() {
    return false;
  }
}

function memory(): KeyValueStorage {
  const data = new Map<string, string>();
  return { get: async (k) => data.get(k) ?? null, set: async (k, v) => void data.set(k, v), remove: async (k) => void data.delete(k) };
}

const hangar: Machine = { machineId: "hangar", machineName: "hangar", baseUrl: HANGAR, addresses: [{ url: HANGAR, kind: "tailnet", learned: false }], token: "hangar-key", deviceId: "d", pairedAt: 1 };
const atlas = { id: "atlas", name: "atlas", baseUrl: ATLAS, addresses: [{ url: ATLAS }] };

async function until(check: () => boolean, ms = 2000) {
  const end = Date.now() + ms;
  while (!check()) {
    if (Date.now() > end) throw new Error("timed out");
    await new Promise((r) => setTimeout(r, 5));
  }
}

async function start() {
  const store = new MachinesStore(memory());
  await store.add(hangar);
  const manager = new FleetManager(store, () => async () => "no-answer", (route, onState, onLive) => new FakeHub(route, onState, onLive) as unknown as MachineHub, new Roster(memory()));
  const home = () => (manager as unknown as { conns: Map<string, never> }).conns.get("hangar")!;
  return { store, manager, home };
}

beforeEach(() => {
  listed = [{ id: "hangar", name: "hangar", baseUrl: HANGAR }];
  yourFleet = true;
  atlasAway = false;
  requests = [];
  globalThis.fetch = (async (input: string, init?: RequestInit) => {
    const url = String(input);
    const auth = new Headers(init?.headers).get("authorization");
    requests.push({ url, method: init?.method ?? "GET", auth });
    const json = (body: unknown) => new Response(JSON.stringify(body), { headers: { "content-type": "application/json" } });
    if (url.startsWith(ATLAS)) return json([]);
    if (!url.startsWith(HANGAR)) throw new TypeError("Network request failed");
    const path = url.slice(HANGAR.length);
    if (path.startsWith("/api/sessions")) return json([]);
    if (path === "/api/machine") return json({ id: "hangar", addresses: [{ url: HANGAR, kind: "tailnet" }] });
    if (path === "/api/preferences") return json(yourFleet ? { YourFleet: "true" } : {});
    if (path === "/api/machines") return json({ machines: listed });
    if (path === "/api/machines/atlas/device-grant" && init?.method === "POST") {
      return atlasAway ? new Response(JSON.stringify({ error: "atlas is away." }), { status: 502 }) : json({ machineId: "atlas", baseUrl: ATLAS, token: "fdt_atlas" });
    }
    return new Response(null, { status: 404 });
  }) as typeof fetch;
});

afterEach(() => {
  globalThis.fetch = realFetch;
});

/** The first pass (when hangar connects) has read the list. */
async function settled(manager: FleetManager) {
  await until(() => manager.snapshot().machines[0]?.state === "connected");
  await new Promise((r) => setTimeout(r, 40));
}

describe("Your Fleet on the phone", () => {
  test("a phone paired with hangar gets its own key for atlas, without announcing a machine that was there before", async () => {
    listed = [...listed, atlas];
    const { store, manager } = await start();
    await until(() => store.get("atlas") !== undefined);

    expect(store.get("atlas")).toMatchObject({ token: "fdt_atlas", via: "hangar", baseUrl: ATLAS, machineName: "atlas" });
    // The grant is asked for with hangar's key, and atlas is then connected with its own.
    expect(requests.find((r) => r.url.endsWith("/device-grant"))!.auth).toBe("Bearer hangar-key");
    await until(() => manager.snapshot().machines.some((m) => m.machineId === "atlas" && m.state === "connected"));
    expect(manager.snapshot().joined).toEqual([]);
  });

  test("atlas added later: the next refresh picks it up and announces it; dismissing clears it", async () => {
    const { store, manager, home } = await start();
    await settled(manager);
    expect(store.get("atlas")).toBeUndefined();

    listed = [...listed, atlas];
    await manager.joinFleet(home());

    expect(store.get("atlas")?.via).toBe("hangar");
    expect(manager.snapshot().joined.map((j) => j.machineName)).toEqual(["atlas"]);
    manager.dismissJoined("atlas");
    expect(manager.snapshot().joined).toEqual([]);
  });

  test("a machine that was away when it was added is announced when it answers", async () => {
    const { store, manager, home } = await start();
    await settled(manager);

    listed = [...listed, atlas];
    atlasAway = true;
    await manager.joinFleet(home());
    expect(store.get("atlas")).toBeUndefined();
    expect(manager.snapshot().joined).toEqual([]);

    atlasAway = false;
    await manager.joinFleet(home());
    expect(manager.snapshot().joined.map((j) => j.machineName)).toEqual(["atlas"]);
  });

  test("with Your Fleet off, the phone asks for nothing", async () => {
    yourFleet = false;
    listed = [...listed, atlas];
    const { store, manager } = await start();
    await settled(manager);

    expect(store.get("atlas")).toBeUndefined();
    expect(requests.some((r) => r.url.endsWith("/device-grant"))).toBe(false);
  });

  test("forgetting atlas on the phone keeps it forgotten", async () => {
    listed = [...listed, atlas];
    const { store, manager, home } = await start();
    await until(() => store.get("atlas") !== undefined);
    await manager.forget("atlas");
    expect(store.get("atlas")).toBeUndefined();

    await manager.joinFleet(home());
    expect(store.get("atlas")).toBeUndefined();
    expect(requests.filter((r) => r.url.endsWith("/device-grant")).length).toBe(1);
  });
});
