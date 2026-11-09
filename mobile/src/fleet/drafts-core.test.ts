import { describe, expect, test } from "bun:test";
import type { KeyValueStorage } from "./machines-core";
import { DraftsStore, draftKey } from "./drafts-core";

function memory() {
  const data = new Map<string, string>();
  const storage: KeyValueStorage = {
    get: async (key) => data.get(key) ?? null,
    set: async (key, value) => void data.set(key, value),
    remove: async (key) => void data.delete(key),
  };
  return { data, storage };
}

describe("DraftsStore", () => {
  test("a draft survives the app being restarted (a new store on the same storage)", async () => {
    const { storage } = memory();
    await new DraftsStore(storage).write("m1", "s1", "fix the login bug");
    expect(await new DraftsStore(storage).read("m1", "s1")).toBe("fix the login bug");
  });
  test("drafts are per machine and session", async () => {
    const { storage } = memory();
    const drafts = new DraftsStore(storage);
    await drafts.write("m1", "s1", "one");
    await drafts.write("m2", "s1", "two");
    expect(await drafts.read("m1", "s1")).toBe("one");
    expect(await drafts.read("m2", "s1")).toBe("two");
    expect(await drafts.read("m1", "s2")).toBe("");
  });
  test("an empty draft (cleared on send) removes the key", async () => {
    const { storage, data } = memory();
    const drafts = new DraftsStore(storage);
    await drafts.write("m1", "s1", "hello");
    await drafts.write("m1", "s1", "");
    expect(data.has(draftKey("m1", "s1"))).toBe(false);
    expect(await drafts.read("m1", "s1")).toBe("");
  });
  test("keys are safe for SecureStore", () => {
    expect(draftKey("a b/c", "d:e")).toBe("fleet.draft.a_b_c.d_e");
  });
  test("a storage failure doesn't throw", async () => {
    const broken: KeyValueStorage = { get: async () => { throw new Error("x"); }, set: async () => { throw new Error("x"); }, remove: async () => { throw new Error("x"); } };
    const drafts = new DraftsStore(broken);
    await drafts.write("m", "s", "t");
    expect(await drafts.read("m", "s")).toBe("");
  });
});
