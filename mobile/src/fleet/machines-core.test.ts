import { describe, expect, test } from "bun:test";
import { MachinesStore, type KeyValueStorage, type Machine } from "./machines-core";

function memory() {
  const data = new Map<string, string>();
  const storage: KeyValueStorage = {
    get: async (key) => data.get(key) ?? null,
    set: async (key, value) => void data.set(key, value),
    remove: async (key) => void data.delete(key),
  };
  return { data, storage };
}

const machine = (id: string, token = "t1"): Machine => ({ machineId: id, machineName: `Box ${id}`, baseUrl: "https://a.example", token, deviceId: "d", pairedAt: 1 });

describe("MachinesStore", () => {
  test("keeps several machines and loads them back", async () => {
    const { storage } = memory();
    const store = new MachinesStore(storage);
    await store.add(machine("a"));
    await store.add(machine("b"));
    const again = new MachinesStore(storage);
    expect((await again.load()).map((m) => m.machineId)).toEqual(["a", "b"]);
  });

  test("pairing the same machine again replaces its entry", async () => {
    const { storage, data } = memory();
    const store = new MachinesStore(storage);
    await store.add(machine("a", "old"));
    await store.add(machine("a", "new"));
    expect(store.all()).toHaveLength(1);
    expect(store.get("a")?.token).toBe("new");
    expect([...data.keys()].filter((k) => k.startsWith("fleet.machine."))).toHaveLength(1);
  });

  test("removing a machine forgets its key and tells subscribers", async () => {
    const { storage, data } = memory();
    const store = new MachinesStore(storage);
    let heard = 0;
    store.subscribe(() => heard++);
    await store.add(machine("a"));
    await store.add(machine("b"));
    await store.remove("a");
    expect(heard).toBe(3);
    expect(store.all().map((m) => m.machineId)).toEqual(["b"]);
    expect(data.has("fleet.machine.a")).toBe(false);
    expect(data.has("fleet.machine.b")).toBe(true);
    expect(JSON.parse(data.get("fleet.machines.v1")!)).toEqual(["b"]);
  });

  test("odd characters in an id stay out of the storage key", async () => {
    const { storage, data } = memory();
    await new MachinesStore(storage).add(machine("a/b c"));
    expect(data.has("fleet.machine.a_b_c")).toBe(true);
  });
});
