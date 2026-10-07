import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { HOME_MACHINE_KEY, loadMachines, loadSessionMachines, saveMachines, setActiveMachine, switchToMachine, type MachineConnection } from "@/lib/machines";
import { useMachinesStore, type MachineInfo } from "@/stores/machines";

// Switching machines reloads the page; here it only records where it went.
vi.mock("@/lib/machines", async (original) => ({ ...await original<typeof import("@/lib/machines")>(), switchToMachine: vi.fn() }));

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

  describe("the list on the server", () => {
    function serverMachine(connection: MachineConnection, withToken = true) {
      return { id: connection.id, name: connection.name, baseUrl: connection.baseUrl, os: connection.os ?? null, status: "online", addedAt: connection.addedAt, lastSeenAt: null, token: withToken ? connection.token : null };
    }

    it("copies this browser's machines to home once, then reads them from there", async () => {
      saveMachines([falcon]);
      const imports: unknown[] = [];
      routes["/api/machines"] = () => json({ machines: [] });
      routes["/api/machines/import"] = (_, init) => {
        imports.push(JSON.parse(String(init.body)));
        return json({ machines: [serverMachine(falcon)] });
      };

      const store = useMachinesStore();
      await store.ready;

      expect(store.source).toBe("server");
      expect(imports).toHaveLength(1);
      expect((imports[0] as { machines: MachineConnection[] }).machines.map((m) => m.id)).toEqual([falcon.id]);
      expect(store.connections.map((c) => c.id)).toEqual([falcon.id]);
      expect(localStorage.getItem("weave:machines-imported")).toBe("true");

      // Another load doesn't import again, even if home has since dropped the machine.
      setActivePinia(createPinia());
      const again = useMachinesStore();
      await again.ready;
      expect(imports).toHaveLength(1);
      expect(again.connections).toEqual([]);
      expect(loadMachines()).toEqual([]);
    });

    it("takes home's list over the cached one", async () => {
      routes["/api/machines"] = () => json({ machines: [serverMachine(falcon)] });

      const store = useMachinesStore();
      await store.ready;

      expect(store.connections.map((c) => c.token)).toEqual([falcon.token]);
      expect(loadMachines().map((m) => m.id)).toEqual([falcon.id]);
    });

    it("keeps this browser's own list when home is too old to keep one", async () => {
      saveMachines([falcon]);

      const store = useMachinesStore();
      await store.ready;

      expect(store.source).toBe("local");
      expect(store.connections.map((c) => c.id)).toEqual([falcon.id]);
      expect(localStorage.getItem("weave:machines-imported")).toBeNull();
    });

    it("writes adds and forgets through to home", async () => {
      const calls: string[] = [];
      routes["/api/machines"] = () => json({ machines: [] });
      routes["/api/machines/import"] = (_, init) => {
        calls.push(`import ${(JSON.parse(String(init.body)) as { machines: MachineConnection[] }).machines.map((m) => m.id).join(",")}`);
        return json({ machines: [] });
      };
      routes[`/api/machines/${falcon.id}`] = (_, init) => {
        calls.push(`${init.method} ${falcon.id}`);
        return new Response(null, { status: 204 });
      };
      const store = useMachinesStore();
      await store.ready;

      await store.addMachine("http://100.64.90.72:2113", falcon.token);
      store.forgetMachine(falcon.id);
      await vi.waitFor(() => expect(calls).toContain(`DELETE ${falcon.id}`));

      expect(calls[0]).toBe(`import ${falcon.id}`);
    });

    it("on a paired phone, lists only machines it has a key for", async () => {
      const osprey = { ...falcon, id: "oooooooooooooooooooooooooooooooo", name: "osprey", baseUrl: "https://osprey.ts.net" };
      routes["/api/machines"] = () => json({ machines: [serverMachine(falcon, false), serverMachine(osprey, false)] });
      localStorage.setItem("weave:device-credentials", JSON.stringify({
        homeMachineId: homeInfo.id, homeMachineName: "kestrel", homeBaseUrl: "", deviceId: "d", token: "fdt_d.x",
        grants: [{ machineId: osprey.id, baseUrl: osprey.baseUrl, token: "fdt_grant.y" }], pairedAt: "",
      }));

      const store = useMachinesStore();
      await store.ready;

      expect(store.source).toBe("device");
      expect(store.connections.map((c) => [c.id, c.token])).toEqual([[osprey.id, "fdt_grant.y"]]);
    });
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

    it("notices when the live machine stops answering, and when it's back", async () => {
      setActiveMachine(falcon);
      const store = useMachinesStore();

      await store.checkLive();
      expect(store.liveReachable).toBe(true);

      routes["http://100.64.90.72:2113/api/machine"] = () => {
        throw new TypeError("Failed to fetch");
      };
      await store.checkLive();
      expect(store.liveReachable).toBe(false);

      routes["http://100.64.90.72:2113/api/machine"] = () => json(falconInfo);
      await store.checkLive();
      expect(store.liveReachable).toBe(true);
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

    it("keeps each session's project, pin and lineage, and the machine's projects, across a reload", async () => {
      const projects = [
        { id: "p-lighthouse", name: "Lighthouse", type: "user", position: 1, description: null, sessionCount: 2, createdAt: "", updatedAt: "" },
        { id: "p-scratch", name: "Scratch", type: "scratch", position: 0, description: null, sessionCount: 0, createdAt: "", updatedAt: "" },
      ];
      routes["http://100.64.90.72:2113/api/projects"] = (_, init) => {
        expect(new Headers(init.headers).get("Authorization")).toBe(`Bearer ${falcon.token}`);
        return json(projects);
      };
      routes["http://100.64.90.72:2113/api/sessions"] = () => json([
        { ...session("pinned", "Spring planting"), projectId: "p-lighthouse", projectName: "Lighthouse", pinOrder: 1 },
        { ...session("fork", "Fork of it"), projectId: "p-lighthouse", projectName: "Lighthouse", forkedFromSessionId: "pinned", spawnKind: "fork", runningWorkCount: 2 },
      ]);
      await useMachinesStore().refreshMachine(falcon.id);

      setActivePinia(createPinia());
      routes["http://100.64.90.72:2113/api/sessions"] = () => {
        throw new TypeError("Failed to fetch");
      };
      const reloaded = useMachinesStore();
      await reloaded.refreshMachine(falcon.id);

      const [pinned, fork] = reloaded.others[falcon.id].sessions;
      expect(pinned).toMatchObject({ projectId: "p-lighthouse", projectName: "Lighthouse", pinOrder: 1 });
      expect(fork).toMatchObject({ forkedFromSessionId: "pinned", spawnKind: "fork", runningWorkCount: 2 });
      expect(reloaded.others[falcon.id].projects).toEqual([
        { id: "p-lighthouse", name: "Lighthouse", type: "user", position: 1 },
        { id: "p-scratch", name: "Scratch", type: "scratch", position: 0 },
      ]);
    });

    it("still lists the sessions when the machine's projects don't answer", async () => {
      routes["http://100.64.90.72:2113/api/sessions"] = () => json([{ ...session("remote-1", "Kept"), projectId: "p-lighthouse", projectName: "Lighthouse" }]);
      routes["http://100.64.90.72:2113/api/projects"] = () => json({ error: "no" }, 500);
      const store = useMachinesStore();

      await store.refreshMachine(falcon.id);

      expect(store.others[falcon.id].sessions).toHaveLength(1);
      expect(store.others[falcon.id].projects).toEqual([]);
      expect(store.others[falcon.id].error).toBeNull();
    });

    it("keeps the live machine's list when switching away, so it shows at once after the reload", () => {
      const store = useMachinesStore();

      store.openOn(falcon.id, "/sessions/remote-1", {
        sessions: [{ ...session("home-1", "Local work"), projectId: "p-1", projectName: "weave-fleet" }] as never,
        projects: [{ id: "p-1", name: "weave-fleet", type: "user", position: 1 }],
      });

      expect(switchToMachine).toHaveBeenCalledWith(falcon.id, "/sessions/remote-1");
      setActiveMachine(falcon);
      setActivePinia(createPinia());
      const reloaded = useMachinesStore();
      expect(reloaded.liveKey).toBe(falcon.id);
      expect(reloaded.others[HOME_MACHINE_KEY].sessions[0]).toMatchObject({ projectId: "p-1", session: { title: "Local work" } });
      expect(reloaded.others[HOME_MACHINE_KEY].projects.map((project) => project.name)).toEqual(["weave-fleet"]);
    });

    it("forgets a machine without touching its sessions", () => {
      const store = useMachinesStore();

      store.forgetMachine(falcon.id);

      expect(loadMachines()).toEqual([]);
      expect(store.hasMachines).toBe(false);
    });
  });
});
