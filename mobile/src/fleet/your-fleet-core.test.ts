import { describe, expect, test } from "bun:test";
import type { KeyValueStorage, Machine } from "./machines-core";
import { isYourFleetOn, joinedText, machineFromGrant, machinesToJoin, Roster } from "./your-fleet-core";

function memory(): KeyValueStorage {
  const data = new Map<string, string>();
  return { get: async (k) => data.get(k) ?? null, set: async (k, v) => void data.set(k, v), remove: async (k) => void data.delete(k) };
}

const paired = (id: string, via?: string): Machine => ({ machineId: id, machineName: id, baseUrl: `https://${id}.example`, token: "t", deviceId: "d", pairedAt: 1, via });

describe("machinesToJoin", () => {
  const listed = [{ id: "hangar", baseUrl: "https://hangar.example" }, { id: "atlas", baseUrl: "https://atlas.example" }, { id: "falcon", baseUrl: "https://falcon.example" }];

  test("leaves out home, machines the phone has a key for and machines it was told to forget", () => {
    expect(machinesToJoin(listed, [paired("hangar"), paired("falcon", "hangar")], "hangar", new Set()).map((m) => m.id)).toEqual(["atlas"]);
    expect(machinesToJoin(listed, [paired("hangar")], "hangar", new Set(["falcon"])).map((m) => m.id)).toEqual(["atlas"]);
  });
});

describe("machineFromGrant", () => {
  const grant = { machineId: "atlas", baseUrl: "https://atlas.tail4f2a.ts.net", token: "fdt_abc" };

  test("keeps home's addresses in its order, and the key", () => {
    const made = machineFromGrant(
      { id: "atlas", name: "atlas", baseUrl: grant.baseUrl, addresses: [{ url: "http://192.168.1.20:6262" }, { url: grant.baseUrl }] },
      grant, "hangar", 5,
    )!;
    expect(made).toMatchObject({ machineId: "atlas", machineName: "atlas", token: "fdt_abc", via: "hangar", baseUrl: grant.baseUrl, pairedAt: 5 });
    expect(made.addresses!.map((a) => a.url)).toEqual(["http://192.168.1.20:6262", grant.baseUrl]);
  });

  test("won't send a key over plain http to a host name, and has nothing to offer when no address is left", () => {
    const plain = "http://atlas.example:6262";
    expect(machineFromGrant({ id: "atlas", baseUrl: plain, addresses: [{ url: plain }] }, { ...grant, baseUrl: plain }, "hangar", 5)).toBeNull();
    const withHttps = machineFromGrant({ id: "atlas", baseUrl: plain, addresses: [{ url: plain }, { url: grant.baseUrl }] }, { ...grant, baseUrl: plain }, "hangar", 5)!;
    expect(withHttps.baseUrl).toBe(grant.baseUrl);
    expect(withHttps.addresses!.map((a) => a.url)).toEqual([grant.baseUrl]);
  });
});

describe("Roster", () => {
  test("the first list is not news; a machine that appears later is, until the phone has its key", async () => {
    const storage = memory();
    expect([...(await new Roster(storage).see(["hangar", "falcon"]))]).toEqual([]);

    const later = new Roster(storage);
    expect([...(await later.see(["hangar", "falcon", "atlas"]))]).toEqual(["atlas"]);
    expect([...(await later.see(["hangar", "falcon", "atlas"]))]).toEqual(["atlas"]);
    await later.joined("atlas");
    expect([...(await new Roster(storage).see(["hangar", "falcon", "atlas"]))]).toEqual([]);
  });

  test("remembers a removal until the machine is paired by hand again", async () => {
    const storage = memory();
    const roster = new Roster(storage);
    await roster.decline("atlas");
    expect([...(await new Roster(storage).declined())]).toEqual(["atlas"]);
    await roster.undecline("atlas");
    expect([...(await new Roster(storage).declined())]).toEqual([]);
  });
});

test("words", () => {
  expect(joinedText({ machineName: "atlas" })).toBe("atlas joined your Fleet");
  expect(isYourFleetOn({ YourFleet: "true" })).toBe(true);
  expect(isYourFleetOn({ YourFleet: "false" })).toBe(false);
  expect(isYourFleetOn(null)).toBe(false);
});
