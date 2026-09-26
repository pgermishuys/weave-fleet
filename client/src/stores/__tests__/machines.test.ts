import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { HOME_MACHINE_KEY, loadMachines, loadSessionMachines, saveMachines, setActiveMachine, type MachineConnection } from "@/lib/machines";
import { useMachinesStore, type MachineInfo } from "@/stores/machines";

const homeInfo: MachineInfo = {
  id: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
  name: "kestrel",
  hostName: "kestrel",
  os: "linux",
  version: "0.36.0",
  apiVersion: 1,
  authMode: "token",
  remoteReachable: false,
  requiresToken: false,
};

const falconInfo: MachineInfo = { ...homeInfo, id: "ffffffffffffffffffffffffffffffff", name: "falcon", hostName: "falcon", os: "macos" };

const falcon: MachineConnection = {
  id: falconInfo.id,
  name: "falcon",
  baseUrl: "http://100.64.90.72:2113",
  token: "falcon-token-0123456789",
  os: "macos",
  addedAt: "2026-09-26T00:00:00.000Z",
};

type Route = (url: string, init: RequestInit) => Response | Promise<Response>;

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

function session(id: string, title: string) {
  return {
    session: { id, title, time: { created: Date.now(), updated: Date.now() } },
    instanceId: `inst-${id}`,
    sessionStatus: "idle",
    activityStatus: "idle",
    retentionStatus: "active",
    parentSessionId: null,
  };
}

describe("machines store", () => {
  let routes: Record<string, Route>;
  let fetchMock: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    localStorage.clear();
    sessionStorage.clear();
    setActiveMachine(null);
    setActivePinia(createPinia());
    routes = {
      "/api/machine": () => json(homeInfo),
      "http://100.64.90.72:2113/api/machine": (_, init) =>
        new Headers(init.headers).get("Authorization") === `Bearer ${falcon.token}` ? json(falconInfo) : json({ error: "no" }, 401),
    };
    fetchMock = vi.fn(async (input: RequestInfo | URL, init: RequestInit = {}) => {
      const url = String(input);
      const path = url.split("?")[0];
      const route = routes[path];
      return route ? route(url, init) : json({ error: `no route ${path}` }, 404);
    });
    vi.stubGlobal("fetch", fetchMock);
  });

  afterEach(() => {
    setActiveMachine(null);
    vi.unstubAllGlobals();
    vi.useRealTimers();
  });

  describe("adding a machine", () => {
    it("checks the address and token with the machine, then keeps it", async () => {
      const store = useMachinesStore();

      const added = await store.addMachine("100.64.90.72:2113/", falcon.token);

      expect(added).toMatchObject({ id: falconInfo.id, name: "falcon", baseUrl: "http://100.64.90.72:2113", os: "macos" });
      expect(loadMachines().map((machine) => machine.id)).toEqual([falconInfo.id]);
      expect(store.entries.map((entry) => entry.name)).toEqual(["kestrel", "falcon"]);
    });

    it("says so when the token is wrong", async () => {
      const store = useMachinesStore();

      await expect(store.addMachine("http://100.64.90.72:2113", "wrong-token-0123456789")).rejects.toThrow("token wasn't accepted");
      expect(loadMachines()).toEqual([]);
    });

    it("says so when nothing answers", async () => {
      routes["http://100.64.90.72:2113/api/machine"] = () => {
        throw new TypeError("Failed to fetch");
      };
      const store = useMachinesStore();

      await expect(store.addMachine("http://100.64.90.72:2113", falcon.token)).rejects.toThrow("Couldn't reach");
    });

    it("refuses a Fleet that speaks another machine contract", async () => {
      routes["http://100.64.90.72:2113/api/machine"] = () => json({ ...falconInfo, apiVersion: 2 });
      const store = useMachinesStore();

      await expect(store.addMachine("http://100.64.90.72:2113", falcon.token)).rejects.toThrow("machine contract 2");
    });

    it("refuses a Fleet too old to have an identity", async () => {
      routes["http://100.64.90.72:2113/api/machine"] = () => json({}, 404);
      const store = useMachinesStore();

      await expect(store.addMachine("http://100.64.90.72:2113", falcon.token)).rejects.toThrow("too old");
    });

    it("refuses this machine under another address", async () => {
      routes["http://100.64.90.72:2113/api/machine"] = () => json(homeInfo);
      const store = useMachinesStore();

      await expect(store.addMachine("http://100.64.90.72:2113", falcon.token)).rejects.toThrow("That's this machine");
    });

    it("updates a machine added twice instead of listing it twice", async () => {
      const store = useMachinesStore();
      await store.addMachine("http://100.64.90.72:2113", falcon.token);
      routes["http://falcon.tail9c2e.ts.net:2113/api/machine"] = () => json(falconInfo);

      await store.addMachine("http://falcon.tail9c2e.ts.net:2113", falcon.token);

      expect(loadMachines()).toHaveLength(1);
      expect(loadMachines()[0].baseUrl).toBe("http://falcon.tail9c2e.ts.net:2113");
    });
  });

  describe("machines that aren't live", () => {
    beforeEach(() => {
      saveMachines([falcon]);
    });

    it("lists another machine's sessions with its token and remembers where they live", async () => {
      routes["http://100.64.90.72:2113/api/sessions"] = (_, init) => {
        expect(new Headers(init.headers).get("Authorization")).toBe(`Bearer ${falcon.token}`);
        expect(init.credentials).toBe("omit");
        return json([session("remote-1", "Notarisation retry loop")]);
      };
      const store = useMachinesStore();

      await store.refreshMachine(falcon.id);

      expect(store.others[falcon.id].sessions.map((item) => item.session.title)).toEqual(["Notarisation retry loop"]);
      expect(store.others[falcon.id].error).toBeNull();
      expect(loadSessionMachines()["remote-1"]).toBe(falcon.id);
    });

    it("keeps the last list and says why when a machine goes away", async () => {
      routes["http://100.64.90.72:2113/api/sessions"] = () => json([session("remote-1", "Kept")]);
      const store = useMachinesStore();
      await store.refreshMachine(falcon.id);

      routes["http://100.64.90.72:2113/api/sessions"] = () => {
        throw new TypeError("Failed to fetch");
      };
      await store.refreshMachine(falcon.id);

      expect(store.others[falcon.id].sessions).toHaveLength(1);
      expect(store.others[falcon.id].error).toBe("Can't reach falcon.");
    });

    it("still lists a machine's sessions after a reload while it's away", async () => {
      routes["http://100.64.90.72:2113/api/sessions"] = () => json([session("remote-1", "Kept")]);
      await useMachinesStore().refreshMachine(falcon.id);

      setActivePinia(createPinia());
      routes["http://100.64.90.72:2113/api/sessions"] = () => {
        throw new TypeError("Failed to fetch");
      };
      const reloaded = useMachinesStore();
      await reloaded.refreshMachine(falcon.id);

      expect(reloaded.others[falcon.id].sessions.map((item) => item.session.title)).toEqual(["Kept"]);
      expect(reloaded.others[falcon.id].error).toBe("Can't reach falcon.");
    });

    it("lists home the same-origin way when another machine is live", async () => {
      setActiveMachine(falcon);
      routes["/api/sessions"] = (_, init) => {
        expect(init.credentials).toBe("include");
        expect(new Headers(init.headers).has("Authorization")).toBe(false);
        return json([session("home-1", "Local work")]);
      };
      const store = useMachinesStore();

      await store.refreshOthers();

      expect(store.liveKey).toBe(falcon.id);
      expect(store.others[HOME_MACHINE_KEY].sessions).toHaveLength(1);
      expect(fetchMock.mock.calls.some(([url]) => String(url).startsWith("http://100.64.90.72:2113/api/sessions"))).toBe(false);
      expect(loadSessionMachines()["home-1"]).toBe(HOME_MACHINE_KEY);
    });

    it("renames a machine on the machine itself", async () => {
      routes["http://100.64.90.72:2113/api/machine"] = (_, init) => {
        expect(init.method).toBe("PUT");
        return json({ ...falconInfo, name: "hangar" });
      };
      const store = useMachinesStore();

      await store.renameMachine(falcon.id, "hangar");

      expect(loadMachines()[0].name).toBe("hangar");
    });

    it("forgets a machine without touching its sessions", () => {
      const store = useMachinesStore();

      store.forgetMachine(falcon.id);

      expect(loadMachines()).toEqual([]);
      expect(store.hasMachines).toBe(false);
    });
  });
});
