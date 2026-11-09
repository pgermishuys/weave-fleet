import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { reactive } from "vue";
import type { SessionListItem } from "@/api/client";
import { resetReferableSessions, useReferableMachines } from "@/composables/use-referable-sessions";
import type { MachineTarget } from "@/lib/machine-target";
import { flushAll, mountComposable } from "./test-utils";

const { apiFetchOn, sessionsGet, store } = vi.hoisted(() => ({
  apiFetchOn: vi.fn(),
  sessionsGet: vi.fn(),
  store: { value: null as unknown },
}));

vi.mock("@/lib/api-client", () => ({ apiFetchOn }));
vi.mock("@/stores/machines", () => ({ useMachinesStore: () => store.value }));
vi.mock("@/stores/sessions", async () => {
  const { ref } = await import("vue");
  const { defineStore } = await import("pinia");
  return { useSessionsStore: defineStore("sessions-test", () => ({ sessions: ref([]) })) };
});

function session(id: string, title: string): SessionListItem {
  return {
    instanceId: "i",
    workspaceId: "w",
    workspaceDirectory: "/work/harbour-app",
    sessionStatus: "idle",
    session: { id, title, time: { created: 1, updated: 1 } },
    retentionStatus: "active",
    harnessType: "opencode2",
  } as SessionListItem;
}

const api = { GET: sessionsGet } as unknown as MachineTarget["api"];
const falcon: MachineTarget = { key: "home", connection: null, isLive: true, api };

function entry(key: string, name: string, isHome = false) {
  return { key, name, isHome, isLive: isHome, connection: isHome ? null : { id: key, name, baseUrl: `http://${name}.test`, token: "t", addedAt: "" } };
}

function useStore(entries: ReturnType<typeof entry>[]) {
  store.value = reactive({
    entries,
    home: { id: "m-falcon" },
    liveKey: "home",
    others: {},
    targetOfKey: (key: string) => ({ key, connection: null, isLive: false, api }),
  });
}

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

function falconSays(machineReferences: boolean, listed: { id: string; status?: string; agentWork?: string }[]) {
  apiFetchOn.mockImplementation(async (_connection: unknown, path: string) => path === "/api/machine"
    ? json({ id: "m-falcon", capabilities: { machineReferences } })
    : json({ machines: listed }));
}

async function machinesFor(target = falcon) {
  const { result, wrapper } = await mountComposable(() => useReferableMachines(true, target));
  await flushAll();
  await flushAll();
  wrapper.unmount();
  return result.value;
}

describe("useReferableMachines", () => {
  beforeEach(() => {
    resetReferableSessions();
    apiFetchOn.mockReset();
    sessionsGet.mockReset();
    sessionsGet.mockResolvedValue({ data: [session("far-1", "Tide chart caching")], response: { ok: true } });
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it("with no other machine listed, offers none and asks nothing", async () => {
    useStore([entry("home", "falcon", true)]);

    const machines = await machinesFor();

    expect(machines.others).toEqual([]);
    expect(apiFetchOn).not.toHaveBeenCalled();
    expect(sessionsGet).not.toHaveBeenCalled();
  });

  it("offers each other machine's sessions, and the ones its machine can reach can be picked", async () => {
    useStore([entry("home", "falcon", true), entry("m-kestrel", "kestrel")]);
    falconSays(true, [{ id: "m-kestrel", status: "online", agentWork: "ask" }]);

    const machines = await machinesFor();

    expect(machines.own).toEqual({ key: "home", name: "falcon" });
    expect(machines.others).toHaveLength(1);
    expect(machines.others[0]).toMatchObject({ key: "m-kestrel", id: "m-kestrel", name: "kestrel", unreachable: null, fix: null });
    expect(machines.others[0].sessions.map((item) => item.session.title)).toEqual(["Tide chart caching"]);
  });

  it("dims a machine the session's machine doesn't list, is set to Never for, or can't reach, with a way to fix it", async () => {
    useStore([entry("home", "falcon", true), entry("m-hangar", "hangar"), entry("m-osprey", "osprey"), entry("m-kite", "kite")]);
    falconSays(true, [{ id: "m-osprey", status: "online", agentWork: "never" }, { id: "m-kite", status: "unreachable" }]);

    const [hangar, osprey, kite] = (await machinesFor()).others;

    expect(hangar.unreachable).toBe("falcon can't reach hangar, so its agent couldn't read these.");
    expect(hangar.fix?.label).toBe("Let falcon reach hangar");
    expect(osprey.unreachable).toContain("set to Never");
    expect(kite.unreachable).toBe("falcon can't reach kite right now, so its agent couldn't read these.");
  });

  it("dims every other machine when the session's machine is a Fleet too old to take such a reference", async () => {
    useStore([entry("home", "falcon", true), entry("m-kestrel", "kestrel")]);
    apiFetchOn.mockImplementation(async (_connection: unknown, path: string) => path === "/api/machine"
      ? json({ id: "m-falcon", capabilities: { peerMessages: true } })
      : json({ machines: [{ id: "m-kestrel", status: "online" }] }));

    const [kestrel] = (await machinesFor()).others;

    expect(kestrel.unreachable).toBe("Fleet on falcon is too old to reference another machine's sessions. Update it there.");
    expect(kestrel.fix).toBeNull();
  });
});
