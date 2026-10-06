import "fake-indexeddb/auto";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { clearCredentials, readCredentials, saveCredentials, type DeviceCredentials } from "@/lib/device-credentials";
import { machinesNeedingGrants, unreachableReason } from "@/lib/phone/grants";
import { useMachineGrants } from "../phone/use-machine-grants";

const credentials: DeviceCredentials = {
  homeMachineId: "home",
  homeMachineName: "hangar",
  homeBaseUrl: "https://hangar.ts.net",
  deviceId: "d1",
  token: "fdt_home.secret",
  grants: [],
  pairedAt: "2026-10-05T09:00:00Z",
};

const falcon = { id: "falcon", name: "falcon", baseUrl: "https://falcon.ts.net" };
const osprey = { id: "osprey", name: "osprey", baseUrl: "http://100.64.1.2:2113" };

describe("machinesNeedingGrants", () => {
  it("skips home, machines it has a key for, and http machines on an https page", () => {
    const withFalcon = { ...credentials, grants: [{ machineId: "falcon", baseUrl: falcon.baseUrl, token: "t" }] };
    const home = { id: "home", name: "hangar", baseUrl: "https://hangar.ts.net" };

    expect(machinesNeedingGrants([home, falcon, osprey], credentials, "https:").map((m) => m.id)).toEqual(["falcon"]);
    expect(machinesNeedingGrants([home, falcon, osprey], withFalcon, "https:")).toEqual([]);
    expect(machinesNeedingGrants([osprey], credentials, "http:").map((m) => m.id)).toEqual(["osprey"]);
    expect(unreachableReason(osprey, "https:")).toContain("Reachable only from computers");
  });
});

describe("useMachineGrants", () => {
  let grantCalls: string[];

  beforeEach(async () => {
    await clearCredentials();
    localStorage.clear();
    await saveCredentials(credentials);
    grantCalls = [];
    vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init: RequestInit = {}) => {
      const url = String(input);
      const machineId = /\/api\/machines\/([^/]+)\/device-grant/.exec(url)?.[1];
      if (machineId) {
        grantCalls.push(`${machineId} ${new Headers(init.headers).get("Authorization")}`);
        if (machineId === "broken") return new Response(JSON.stringify({ error: "Can't reach broken from hangar right now." }), { status: 502 });
        return new Response(JSON.stringify({ machineId, baseUrl: `https://${machineId}.ts.net`, token: `fdt_${machineId}.${grantCalls.length}` }), { status: 200 });
      }
      return new Response("{}", { status: 404 });
    }));
  });

  afterEach(() => vi.unstubAllGlobals());

  it("asks home for a key on each machine it lacks one for, and keeps them", async () => {
    const grants = useMachineGrants();

    await grants.ensureGrants([falcon]);
    await grants.ensureGrants([falcon]);

    expect(grantCalls).toEqual(["falcon Bearer fdt_home.secret"]);
    expect((await readCredentials())?.grants).toEqual([{ machineId: "falcon", baseUrl: "https://falcon.ts.net", token: "fdt_falcon.1" }]);
  });

  it("says why a machine has no key", async () => {
    const grants = useMachineGrants();

    await grants.ensureGrants([{ id: "broken", name: "broken", baseUrl: "https://broken.ts.net" }]);

    expect(grants.problems.value.broken).toBe("Can't reach broken from hangar right now.");
  });

  it("renews a key a machine turned away, once", async () => {
    const grants = useMachineGrants();
    await grants.ensureGrants([falcon]);

    expect(await grants.renewGrant("falcon")).toBe("fdt_falcon.2");
    expect(await grants.renewGrant("falcon")).toBeNull();
    expect((await readCredentials())?.grants.map((g) => g.token)).toEqual(["fdt_falcon.2"]);
  });
});
