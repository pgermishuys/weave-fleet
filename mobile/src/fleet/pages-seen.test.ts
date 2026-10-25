import { describe, expect, test } from "bun:test";
import type { KeyValueStorage } from "./machines-core";
import { PagesSeenStore, newPagesOf } from "./pages-seen-core";

function memory() {
  const data = new Map<string, string>();
  const storage: KeyValueStorage = {
    get: async (key) => data.get(key) ?? null,
    set: async (key, value) => void data.set(key, value),
    remove: async (key) => void data.delete(key),
  };
  return { data, storage };
}

const since = "2026-10-24T09:00:00.000Z";

describe("newPagesOf", () => {
  test("a session with no pages has none new", () => {
    expect(newPagesOf({ pageCount: 0, lastPageShownAt: null }, null, since)).toBe(0);
    expect(newPagesOf({}, null, since)).toBe(0);
  });
  test("a session the phone never opened counts pages shown since it started keeping track", () => {
    expect(newPagesOf({ pageCount: 2, lastPageShownAt: "2026-10-24T10:00:00.0000000+00:00" }, null, since)).toBe(2);
    expect(newPagesOf({ pageCount: 3, lastPageShownAt: "2026-10-24T08:00:00.0000000+00:00" }, null, since)).toBe(0);
  });
  test("after Pages was opened, only pages shown since are new", () => {
    const seen = { at: "2026-10-24T11:00:00.000Z", count: 2 };
    expect(newPagesOf({ pageCount: 2, lastPageShownAt: "2026-10-24T10:59:00.0000000+00:00" }, seen, since)).toBe(0);
    expect(newPagesOf({ pageCount: 4, lastPageShownAt: "2026-10-24T11:05:00.0000000+00:00" }, seen, since)).toBe(2);
  });
  test("a page shown again, or one closed and another shown, is still one new page", () => {
    const seen = { at: "2026-10-24T11:00:00.000Z", count: 3 };
    expect(newPagesOf({ pageCount: 3, lastPageShownAt: "2026-10-24T11:05:00.0000000+00:00" }, seen, since)).toBe(1);
    expect(newPagesOf({ pageCount: 2, lastPageShownAt: "2026-10-24T11:05:00.0000000+00:00" }, seen, since)).toBe(1);
  });
});

describe("PagesSeenStore", () => {
  test("starts keeping track once, and keeps that time", async () => {
    const { storage } = memory();
    const first = new PagesSeenStore(storage, () => new Date(since));
    await first.load();
    const again = new PagesSeenStore(storage, () => new Date("2026-10-30T00:00:00.000Z"));
    await again.load();
    expect(again.since).toBe(since);
  });

  test("reads a session's seen time on demand and tells listeners when it lands", async () => {
    const { storage } = memory();
    await storage.set("fleet.pages-seen.box-1.s1", JSON.stringify({ at: "2026-10-24T11:00:00.000Z", count: 2 }));
    const store = new PagesSeenStore(storage);
    let heard = 0;
    store.subscribe(() => heard++);
    expect(store.get("box-1", "s1")).toBeUndefined();
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(store.get("box-1", "s1")).toEqual({ at: "2026-10-24T11:00:00.000Z", count: 2 });
    expect(store.get("box-1", "s2")).toBeUndefined();
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(store.get("box-1", "s2")).toBeNull();
    expect(heard).toBe(2);
  });

  test("marking Pages seen keeps it per machine and session, under a key SecureStore accepts", async () => {
    const { storage, data } = memory();
    const store = new PagesSeenStore(storage, () => new Date("2026-10-24T12:00:00.000Z"));
    await store.markSeen("https://box:5441", "s1", 3);
    expect(store.get("https://box:5441", "s1")).toEqual({ at: "2026-10-24T12:00:00.000Z", count: 3 });
    expect([...data.keys()]).toEqual(["fleet.pages-seen.https___box_5441.s1"]);
    expect(new PagesSeenStore(storage).get("https://box:5441", "s2")).toBeUndefined();
  });
});
